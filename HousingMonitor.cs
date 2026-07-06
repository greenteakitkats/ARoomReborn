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

        var current = BuildSnapshot(furnitureManager);
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
        foreach (var change in LayoutDiffer.Diff(oldSet, newSet))
        {
            switch (change.Action)
            {
                case HistoryAction.Placed:
                    LogSimple(HistoryAction.Placed, change.Index, change.After, houseId, location);
                    break;
                case HistoryAction.Removed:
                    LogSimple(HistoryAction.Removed, change.Index, change.Before, houseId, location);
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

    // Prefer the resolved sheet row for naming; fall back to the raw id so the log shows
    // a meaningful "#<rawId>" rather than "#0" if row resolution isn't working yet.
    private static uint DisplayId(FurnitureRecord r) => r.RowId != 0 ? r.RowId : r.Id;

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

    private static unsafe Dictionary<int, FurnitureRecord>? BuildSnapshot(HousingFurnitureManager* furnitureManager)
    {
        var map = new Dictionary<int, FurnitureRecord>();

        // FurnitureMemory is the generated accessor for `_furnitureMemory` (length 1462).
        // Index 1461 is the temporary/preview object while dragging, so skip the last slot.
        var span = furnitureManager->FurnitureMemory;
        for (var i = 0; i < span.Length - 1; i++)
        {
            ref var f = ref span[i];
            if (f.Id == 0)
                continue; // empty slot

            var pos = new Vector3(f.Position.X, f.Position.Y, f.Position.Z);
            if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z))
                return null; // garbage read (e.g. shifted offsets), signal a bad snapshot

            map[f.Index] = new FurnitureRecord(f.Id, ReadRowId(furnitureManager, f.Index), pos, f.Rotation, f.Stain);
        }

        return map;
    }

    /// <summary>
    /// The HousingFurniture sheet row for a placed object, read from the furniture game
    /// object (its GimmickId) via the object manager. This, not HousingFurniture.Id, is
    /// what maps to the item name. See MakePlace for the reference implementation.
    /// </summary>
    private static unsafe uint ReadRowId(HousingFurnitureManager* furnitureManager, int index)
    {
        var objects = &furnitureManager->ObjectManager.ObjectArray;
        if (index < 0 || index >= objects->ObjectCount)
            return 0;

        var gameObject = objects->Objects[index].Value;
        // The HousingFurniture sheet row lives in the game object's BaseId (offset 0x84).
        // Confirmed against ReMakePlace, whose housingRowId is at 0x84 on the current client.
        return gameObject != null ? gameObject->BaseId : 0u;
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

            Plugin.Log.Information($"[dump] IsInside={manager->IsInside()} IsOutside={manager->IsOutside()} HouseId={(ulong)manager->GetCurrentHouseId():X}");

            var furnitureManager = manager->GetFurnitureManager();
            Plugin.Log.Information($"[dump] FurnitureManager: {(furnitureManager == null ? "null" : "ok")}");
            if (furnitureManager == null)
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
                    var objects = &furnitureManager->ObjectManager.ObjectArray;
                    var inRange = f.Index >= 0 && f.Index < objects->ObjectCount;
                    var gobj = inRange ? objects->Objects[f.Index].Value : null;
                    uint layoutId = gobj != null ? gobj->LayoutId : 0;
                    uint gimmickId = gobj != null ? gobj->GimmickId : 0;
                    uint baseId = gobj != null ? gobj->BaseId : 0;

                    Plugin.Log.Information(
                        $"[dump] rawId={f.Id} idx={f.Index} objCount={objects->ObjectCount} obj={(gobj == null ? "null" : "ok")} " +
                        $"layoutId={layoutId} gimmickId={gimmickId} baseId={baseId}");
                    Plugin.Log.Information(
                        $"[dump]   names: gimmick->\"{NameResolver.Resolve(gimmickId)}\" layout->\"{NameResolver.Resolve(layoutId)}\" " +
                        $"base->\"{NameResolver.Resolve(baseId)}\" rawLow->\"{NameResolver.Resolve(f.Id & 0xFFFF)}\"");
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
