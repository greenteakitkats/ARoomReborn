using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace ARoomReborn;

/// <summary>
/// Polls the furniture set, indoors or in the yard depending on where you're standing, and
/// logs placements, removals, moves, rotations, and dye changes by diffing against the
/// previous snapshot. On entering a house or yard it diffs against the last-known layout to
/// surface changes made while you were away. Purely read-only.
/// </summary>
public sealed class HousingMonitor : IDisposable
{
    private const double SaveDebounceSeconds = 15.0;

    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    private readonly Plugin plugin;
    private readonly List<HistoryEntry> entries = new();

    // Last-known full layout per house (houseId -> index -> state), one dictionary for indoor
    // furnishings and one for the yard, since a house's indoor and outdoor items are entirely
    // separate sets. Persisted so we can diff "what changed since last visit".
    private Dictionary<ulong, Dictionary<int, FurnitureRecord>> savedLayouts = new();
    private Dictionary<ulong, Dictionary<int, FurnitureRecord>> savedOutdoorLayouts = new();

    // Live working state for wherever we're currently standing (indoors or in the yard).
    private Dictionary<int, FurnitureRecord> baseline = new();
    private bool haveBaseline;
    private ulong baselineHouseId;
    private HouseLocation baselineLocation;
    private long lastStamp = long.MinValue;
    private DateTime lastPoll = DateTime.MinValue;

    // Furniture streams in after a zone load, often reading empty or partial at first.
    // We ignore empty reads during a grace window and wait for the item count to hold
    // steady for several polls (and a minimum dwell) before trusting it as the baseline,
    // otherwise the load-in reads as a flood of placements every time you enter.
    private const int SettleReads = 3;
    private const double MinDwellSeconds = 5.0;
    private const double EmptyGraceSeconds = 15.0;
    private int settleCount;
    private int settleLastCount = -1;
    private ulong settleHouseId;
    private HouseLocation settleLocation;
    private DateTime houseFirstSeen;

    // When true, entries created by the current diff are tagged as detected-on-entry.
    private bool markAway;

    // Dye previews change an item's stain live, so we hold a dye change until it settles on
    // a color (or is cancelled) rather than logging every color you hover over.
    private const double DyeSettleSeconds = 2.5;
    private readonly Dictionary<int, (byte target, DateTime since)> pendingDye = new();

    // Best-effort "stored vs removed" detection: we can't see where a removed item went, but
    // if the housing storeroom gained items on the same tick a removal was detected, that item
    // was almost certainly stored rather than sent to inventory. We track the storeroom's item
    // count between polls to spot that growth. -1 means "no baseline yet".
    private int prevStoreroomItems = -1;
    private HouseLocation prevStoreroomLoc;

    private bool dirty;
    private DateTime lastSave = DateTime.MinValue;

    public IReadOnlyList<HistoryEntry> Entries => entries;

    public HousingMonitor(Plugin plugin)
    {
        this.plugin = plugin;
        Load();
        Plugin.Framework.Update += OnUpdate;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Save();
    }

    public void Clear()
    {
        entries.Clear();
        dirty = true;
    }

    private void OnTerritoryChanged(uint territory)
    {
        // Changing zones invalidates the live baseline; it re-establishes on the next read.
        haveBaseline = false;
        baseline.Clear();
        settleHouseId = 0;
        settleLocation = default;
        settleLastCount = -1;
        settleCount = 0;
        pendingDye.Clear();
        prevStoreroomItems = -1;
    }

    private void OnUpdate(IFramework framework)
    {
        var interval = Math.Max(0.25f, plugin.Configuration.PollIntervalSeconds);
        if ((DateTime.UtcNow - lastPoll).TotalSeconds >= interval)
        {
            lastPoll = DateTime.UtcNow;

            // Fail soft: a struct mismatch after a patch must degrade gracefully, never
            // crash or spam. Pause until the next zone change re-establishes a baseline.
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Housing read failed; pausing until next zone change.");
                haveBaseline = false;
            }
        }

        MaybeSave();
    }

    private unsafe void Poll()
    {
        var manager = HousingManager.Instance();
        var location = manager != null && manager->IsOutside() ? HouseLocation.Outdoor
            : manager != null && manager->IsInside() ? HouseLocation.Indoor
            : (HouseLocation?)null;
        if (manager == null || location == null)
        {
            haveBaseline = false; // not standing in a readable house or yard
            return;
        }

        // GetFurnitureManager() returns whichever furniture set matches where we're standing,
        // indoor or outdoor, so the rest of the pipeline (BuildSnapshot, LayoutDiffer) is the
        // same for both. Only the saved-layout bucket and the per-entry tag differ.
        var furnitureManager = manager->GetFurnitureManager();
        if (furnitureManager == null)
        {
            haveBaseline = false;
            return;
        }

        // LastUpdate bumps ~every 200ms when furniture state changes. If it hasn't moved
        // since we last processed, there's nothing to diff, skip the snapshot build.
        var stamp = furnitureManager->LastUpdate;
        var houseId = (ulong)manager->GetCurrentHouseId();
        if (haveBaseline && houseId == baselineHouseId && location == baselineLocation && stamp == lastStamp)
            return;
        lastStamp = stamp;

        var current = BuildSnapshot(furnitureManager, location.Value);
        if (current == null)
        {
            haveBaseline = false; // garbage read, wait for a clean one
            return;
        }

        // First read after entering, after moving to a different house (two plots can share a
        // TerritoryType, so we key off HouseId, not territory), or after stepping between
        // indoors and the yard, which is a completely different furniture set.
        if (!haveBaseline || houseId != baselineHouseId || location != baselineLocation)
        {
            // New house/location in view, start the settle/grace timers.
            if (settleHouseId != houseId || settleLocation != location)
            {
                settleHouseId = houseId;
                settleLocation = location.Value;
                settleLastCount = -1;
                settleCount = 0;
                houseFirstSeen = DateTime.UtcNow;
                return;
            }

            var dwell = (DateTime.UtcNow - houseFirstSeen).TotalSeconds;

            // Ignore empty reads while furniture is still streaming in, so an in-progress
            // load isn't mistaken for an empty house (and then flooded as items appear).
            if (current.Count == 0 && dwell < EmptyGraceSeconds)
                return;

            // Require the count to hold steady for a few polls, plus a minimum dwell.
            if (current.Count != settleLastCount)
            {
                settleLastCount = current.Count;
                settleCount = 0;
                return;
            }
            if (++settleCount < SettleReads || dwell < MinDwellSeconds)
                return;

            var savedForLocation = SavedLayoutsFor(location.Value);
            if (savedForLocation.TryGetValue(houseId, out var lastKnown))
            {
                // We've been here before, log only what's different since last visit.
                markAway = true;
                try { DiffAndLog(lastKnown, current, houseId, location.Value, live: false); }
                finally { markAway = false; }
            }
            else
            {
                // First time we've ever seen this house/location, seed silently.
                Plugin.Log.Information($"First visit ({location}) to house {houseId:X}: {current.Count} item(s).");
            }

            baseline = current;
            baselineHouseId = houseId;
            baselineLocation = location.Value;
            haveBaseline = true;
            settleHouseId = 0;
            settleLastCount = -1;
            settleCount = 0;
            pendingDye.Clear();
            RememberLayout(houseId, location.Value, current);
            return;
        }

        DiffAndLog(baseline, current, houseId, location.Value, live: true);
        baseline = MergeBaseline(baseline, current);
        RememberLayout(houseId, location.Value, baseline);
    }

    private Dictionary<ulong, Dictionary<int, FurnitureRecord>> SavedLayoutsFor(HouseLocation location)
        => location == HouseLocation.Outdoor ? savedOutdoorLayouts : savedLayouts;

    private void RememberLayout(ulong houseId, HouseLocation location, Dictionary<int, FurnitureRecord> layout)
    {
        SavedLayoutsFor(location)[houseId] = layout;
        dirty = true;
    }

    private void DiffAndLog(Dictionary<int, FurnitureRecord> oldSet, Dictionary<int, FurnitureRecord> newSet, ulong houseId, HouseLocation location, bool live)
    {
        // How many of this tick's removals went to the storeroom (see prevStoreroomItems). Only
        // live: the away-diff has no before/after storeroom snapshot, so those stay plain Removed.
        var storedThisTick = 0;
        if (live)
        {
            var cur = StoreroomItemCount();
            if (prevStoreroomItems >= 0 && location == prevStoreroomLoc)
                storedThisTick = Math.Max(0, cur - prevStoreroomItems);
            prevStoreroomItems = cur;
            prevStoreroomLoc = location;
        }

        foreach (var change in LayoutDiffer.Diff(oldSet, newSet))
        {
            switch (change.Action)
            {
                case HistoryAction.Placed:
                    LogSimple(HistoryAction.Placed, change.Index, change.After, houseId, location);
                    break;
                case HistoryAction.Removed:
                    // If the storeroom grew this tick, attribute the growth to these removals.
                    var wasStored = storedThisTick > 0;
                    if (wasStored)
                        storedThisTick--;
                    LogSimple(wasStored ? HistoryAction.Stored : HistoryAction.Removed, change.Index, change.Before, houseId, location);
                    pendingDye.Remove(change.Index);
                    break;
                case HistoryAction.Moved:
                case HistoryAction.Rotated:
                    LogMovement(change.Action, change.Index, change.Before, change.After, houseId, location);
                    break;
                case HistoryAction.Redyed:
                    // Live: defer (previews change stain). Away-diff: stable snapshot, log now.
                    if (live)
                        TrackPendingDye(change.Index, change.After.Stain);
                    else
                        LogRedyed(change.Index, change.Before, change.After, houseId, location);
                    break;
            }
        }

        if (live)
            CommitSettledDyes(oldSet, newSet, houseId, location);
    }

    private void TrackPendingDye(int index, byte target)
    {
        // Keep the original timer while the target color is unchanged, so hovering the same
        // swatch accrues toward "settled"; a new color resets it.
        if (pendingDye.TryGetValue(index, out var p) && p.target == target)
            return;
        pendingDye[index] = (target, DateTime.Now);
    }

    private void CommitSettledDyes(Dictionary<int, FurnitureRecord> oldSet, Dictionary<int, FurnitureRecord> newSet, ulong houseId, HouseLocation location)
    {
        if (pendingDye.Count == 0)
            return;

        var done = new List<int>();
        foreach (var (index, p) in pendingDye)
        {
            if (!newSet.TryGetValue(index, out var now))
            {
                done.Add(index); // item removed
                continue;
            }

            var committed = oldSet.TryGetValue(index, out var prev) ? prev : now;
            if (now.Stain == committed.Stain)
            {
                done.Add(index); // back to the committed color (preview cancelled)
                continue;
            }

            if (now.Stain == p.target && (DateTime.Now - p.since).TotalSeconds >= DyeSettleSeconds)
            {
                LogRedyed(index, committed, now, houseId, location);
                done.Add(index);
            }
        }

        foreach (var index in done)
            pendingDye.Remove(index);
    }

    // Adopt the new snapshot, but hold the committed stain for any item with a pending dye,
    // so a preview color doesn't stick and we keep noticing the change until it settles.
    private Dictionary<int, FurnitureRecord> MergeBaseline(Dictionary<int, FurnitureRecord> old, Dictionary<int, FurnitureRecord> current)
    {
        if (pendingDye.Count == 0)
            return current;

        var result = new Dictionary<int, FurnitureRecord>(current.Count);
        foreach (var (index, rec) in current)
        {
            if (pendingDye.ContainsKey(index) && old.TryGetValue(index, out var prev))
                result[index] = rec with { Stain = prev.Stain };
            else
                result[index] = rec;
        }
        return result;
    }

    private static uint DisplayId(FurnitureRecord r) => r.RowId;

    private void LogSimple(HistoryAction action, int index, FurnitureRecord r, ulong houseId, HouseLocation location)
        => AddEntry(new HistoryEntry(DateTime.Now, action, index, DisplayId(r), NameResolver.Resolve(DisplayId(r)),
            r.Position, r.Rotation, null, 0f, r.Stain, r.Stain, houseId, Plugin.ClientState.TerritoryType, markAway, location));

    private void LogRedyed(int index, FurnitureRecord before, FurnitureRecord now, ulong houseId, HouseLocation location)
        => AddEntry(new HistoryEntry(DateTime.Now, HistoryAction.Redyed, index, DisplayId(now), NameResolver.Resolve(DisplayId(now)),
            now.Position, now.Rotation, null, 0f, now.Stain, before.Stain, houseId, Plugin.ClientState.TerritoryType, markAway, location));

    private void LogMovement(HistoryAction action, int index, FurnitureRecord before, FurnitureRecord now, ulong houseId, HouseLocation location)
    {
        // Every detected move/rotate gets its own row, so the log reads as a full history
        // rather than a summary. Consecutive edits of the same item just stack up in order.
        AddEntry(new HistoryEntry(DateTime.Now, action, index, DisplayId(now), NameResolver.Resolve(DisplayId(now)),
            now.Position, now.Rotation, before.Position, before.Rotation, now.Stain, before.Stain,
            houseId, Plugin.ClientState.TerritoryType, markAway, location));
    }

    private void AddEntry(HistoryEntry entry)
    {
        entries.Insert(0, entry);

        var max = Math.Max(10, plugin.Configuration.MaxEntries);
        if (entries.Count > max)
            entries.RemoveRange(max, entries.Count - max);

        dirty = true;
        // Information, not Debug: this is cheap (only fires on an actual detected change) and
        // showing the raw index/id here is what let us diagnose the indoor name-resolution bug
        // in the past; keeping it visible without a log-level change pays off again later.
        Plugin.Log.Information($"[{entry.Location}] {entry.Action}{(entry.WhileAway ? " (away)" : "")}: {entry.ItemName} (#{entry.FurnitureId}) idx={entry.ObjectIndex} @ {entry.Position}");
    }

    private static unsafe Dictionary<int, FurnitureRecord>? BuildSnapshot(HousingFurnitureManager* furnitureManager, HouseLocation location)
    {
        var map = new Dictionary<int, FurnitureRecord>();

        // FurnitureMemory is the generated accessor for `_furnitureMemory` (length 1462).
        // Index 1461 is the temporary/preview object while dragging, so skip the last slot.
        // Key by the array slot itself, not HousingFurniture.Index: that field only points
        // into the live game-object array while the item is actually spawned/streamed in,
        // and outdoors most of the plot isn't loaded at once, so it reads -1 for anything
        // out of range. Every unloaded item collapsing onto the same -1 key is what caused
        // the "kept saying placed and removed" noise in the yard. The array slot itself is
        // the plot's persisted storage position, populated whether or not the item is
        // currently rendered, so it stays stable across streaming.
        var span = furnitureManager->FurnitureMemory;
        for (var i = 0; i < span.Length - 1; i++)
        {
            ref var f = ref span[i];
            if (f.Id == 0)
                continue; // empty slot

            var pos = new Vector3(f.Position.X, f.Position.Y, f.Position.Z);
            if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z))
                return null; // garbage read (e.g. shifted offsets), signal a bad snapshot

            map[i] = new FurnitureRecord(f.Id, ResolveRowId(f.Id, location), pos, f.Rotation, f.Stain);
        }

        return map;
    }

    /// <summary>
    /// The HousingFurniture sheet row for a placed object. Per FFXIVClientStructs'
    /// HousingFurniture.Id docs, the row is (0x20000 | Id) indoors and (0x30000 | Id)
    /// outdoors, so this needs nothing from the item's game object (which may not even
    /// exist yet if it hasn't streamed in), just which side of the door we're standing on.
    /// </summary>
    private static uint ResolveRowId(uint rawId, HouseLocation location)
    {
        var mask = location == HouseLocation.Outdoor ? 0x30000u : 0x20000u;
        return mask | rawId;
    }

    /// <summary>
    /// Total number of items sitting in the housing storeroom containers. Used only to notice
    /// growth between polls (an item being stored), not for exact contents. The storeroom
    /// containers live in the 27000 inventory-type block (exterior plus the interior rooms).
    /// </summary>
    private static unsafe int StoreroomItemCount()
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return 0;

        var total = 0;
        for (var t = 27000; t <= 27020; t++)
        {
            var container = mgr->GetInventoryContainer((InventoryType)t);
            if (container == null || !container->IsLoaded)
                continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId != 0)
                    total++;
            }
        }
        return total;
    }

    /// <summary>
    /// For the smart undo button: find the item this history entry refers to as it sits right
    /// now (same sheet row, still at the entry's "new" position), select it in the game's
    /// housing editor, and snap it back to the entry's previous position and facing, all in one
    /// click. Returns false if the item can't be found (already moved again, or not loaded in),
    /// if we're not in layout edit mode, or if the select-item hook isn't available, in which
    /// case the caller falls back to the manual select-then-undo flow.
    /// </summary>
    public unsafe bool TrySmartUndo(HistoryEntry e)
    {
        if (e.FromPosition is not { } from)
            return false;

        var manager = HousingManager.Instance();
        var furnitureManager = manager != null ? manager->GetFurnitureManager() : null;
        if (furnitureManager == null)
            return false;

        var objects = &furnitureManager->ObjectManager.ObjectArray;
        var span = furnitureManager->FurnitureMemory;
        for (var i = 0; i < span.Length - 1; i++)
        {
            ref var f = ref span[i];
            if (f.Id == 0 || ResolveRowId(f.Id, e.Location) != e.FurnitureId)
                continue;

            var pos = new Vector3(f.Position.X, f.Position.Y, f.Position.Z);
            if (Vector3.Distance(pos, e.Position) > 0.05f)
                continue; // this instance isn't sitting where the entry left it

            // Found it. Its live game object carries the layout instance the editor selects.
            var idx = f.Index;
            if (idx < 0 || idx >= objects->ObjectCount)
                return false; // out of render range, no game object to select
            var gameObject = objects->Objects[idx].Value;
            if (gameObject == null || gameObject->SharedGroupLayoutInstance == null)
                return false;

            return HousingWriter.TrySelectAndUndo(gameObject->SharedGroupLayoutInstance, from, e.FromRotation);
        }

        return false;
    }

    /// <summary>
    /// One-shot diagnostics dump for `/houselog dump`. Logs the raw read so you can
    /// verify after a game/Dalamud patch whether the housing reads still resolve.
    /// </summary>
    public unsafe void LogDiagnostics()
    {
        try
        {
            var manager = HousingManager.Instance();
            Plugin.Log.Information($"[dump] HousingManager: {(manager == null ? "null" : "ok")}");
            if (manager == null)
                return;

            var location = manager->IsOutside() ? HouseLocation.Outdoor
                : manager->IsInside() ? HouseLocation.Indoor
                : (HouseLocation?)null;
            Plugin.Log.Information($"[dump] IsInside={manager->IsInside()} IsOutside={manager->IsOutside()} HouseId={(ulong)manager->GetCurrentHouseId():X}");

            var furnitureManager = manager->GetFurnitureManager();
            Plugin.Log.Information($"[dump] FurnitureManager: {(furnitureManager == null ? "null" : "ok")}");
            if (furnitureManager == null || location == null)
                return;

            var span = furnitureManager->FurnitureMemory;
            var count = 0;
            var shown = 0;
            for (var i = 0; i < span.Length - 1; i++)
            {
                ref var f = ref span[i];
                if (f.Id == 0)
                    continue;

                count++;
                if (shown < 6)
                {
                    // For comparison: whether the item's game object is currently spawned
                    // (only true when it's actually streamed in/rendered).
                    var objects = &furnitureManager->ObjectManager.ObjectArray;
                    var inRange = f.Index >= 0 && f.Index < objects->ObjectCount;
                    var gobj = inRange ? objects->Objects[f.Index].Value : null;

                    var rowId = ResolveRowId(f.Id, location.Value);
                    Plugin.Log.Information(
                        $"[dump] slot={i} rawId={f.Id} objIdx={f.Index} obj={(gobj == null ? "null" : "ok")} " +
                        $"rowId={rowId} name=\"{NameResolver.Resolve(rowId)}\"");
                    shown++;
                }
            }

            Plugin.Log.Information($"[dump] total items: {count}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[dump] diagnostics read failed.");
        }
    }

    // ---- Persistence ----

    private static string HistoryPath
        => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "history.json");

    private static string LayoutsPath
        => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "layouts.json");

    private static string OutdoorLayoutsPath
        => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "layouts_outdoor.json");

    private void MaybeSave()
    {
        if (!dirty || (DateTime.UtcNow - lastSave).TotalSeconds < SaveDebounceSeconds)
            return;
        Save();
    }

    private void Load()
    {
        try
        {
            MigrateOldHistoryIfNeeded();

            if (File.Exists(HistoryPath))
            {
                var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistoryPath), JsonOpts);
                if (loaded != null)
                {
                    entries.Clear();
                    var max = Math.Max(10, plugin.Configuration.MaxEntries);
                    entries.AddRange(loaded.Count > max ? loaded.GetRange(0, max) : loaded);
                }
            }

            if (File.Exists(LayoutsPath))
            {
                savedLayouts = JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<int, FurnitureRecord>>>(
                    File.ReadAllText(LayoutsPath), JsonOpts) ?? new();
            }

            if (File.Exists(OutdoorLayoutsPath))
            {
                savedOutdoorLayouts = JsonSerializer.Deserialize<Dictionary<ulong, Dictionary<int, FurnitureRecord>>>(
                    File.ReadAllText(OutdoorLayoutsPath), JsonOpts) ?? new();
            }
        }
        catch (Exception ex)
        {
            // Non-critical, a bad/old file just means we start with an empty log/layouts.
            Plugin.Log.Warning(ex, "Could not load saved history; starting fresh.");
        }
    }

    // Until v0.13.3 the plugin's InternalName was "HousingHistory", which gave it a different
    // Dalamud config directory. Renaming to "ARoomReborn" started it with an empty log. This
    // does a one-time copy of the old history.json into the new folder so the visible log
    // carries over. We deliberately do NOT bring the old layouts.json/layouts_outdoor.json:
    // those were keyed by the old game-object-index scheme, and diffing the new slot-keyed
    // reads against them would produce a flood of fake changes. The layouts just re-seed
    // silently on the next visit instead, which is correct under the new keying.
    private void MigrateOldHistoryIfNeeded()
    {
        // Only migrate into a genuinely fresh install, never overwrite an existing log.
        if (File.Exists(HistoryPath))
            return;

        var parent = Plugin.PluginInterface.ConfigDirectory.Parent?.FullName;
        if (parent == null)
            return;

        var oldHistory = Path.Combine(parent, "HousingHistory", "history.json");
        if (!File.Exists(oldHistory))
            return;

        try
        {
            Plugin.PluginInterface.ConfigDirectory.Create();
            File.Copy(oldHistory, HistoryPath);
            Plugin.Log.Information("Imported the log from the previous HousingHistory config folder.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not import the old history; starting with an empty log.");
        }
    }

    private void Save()
    {
        try
        {
            Plugin.PluginInterface.ConfigDirectory.Create();
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(entries, JsonOpts));
            File.WriteAllText(LayoutsPath, JsonSerializer.Serialize(savedLayouts, JsonOpts));
            File.WriteAllText(OutdoorLayoutsPath, JsonSerializer.Serialize(savedOutdoorLayouts, JsonOpts));
            dirty = false;
            lastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not save history.");
        }
    }
}
