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

    // Item-level storeroom/inventory tracking, for transfers that never touch the room at all
    // (moving something straight from your bags into the storeroom or back). Separate from the
    // aggregate count above: that one correlates storeroom growth with a furniture removal from
    // the room, this one correlates storeroom contents against your regular inventory contents,
    // so an item leaving one and landing in the other in the same tick is a direct transfer.
    private Dictionary<uint, int> prevStoreroomContents = new();
    private Dictionary<uint, int> prevInventoryContents = new();
    private bool haveStorageBaseline;

    // This tick's item-id deltas, valid for the current Poll() call only: computed early,
    // partly consumed by DiffAndLog when a furniture Placed/Removed correlates with one
    // unambiguously (see TryResolveViaStorageDelta), whatever's left gets logged as a direct
    // Deposited/Withdrawn transfer at the end of Poll().
    private readonly Dictionary<uint, int> tickStoreroomDelta = new();
    private readonly Dictionary<uint, int> tickInventoryDelta = new();

    private bool dirty;
    private DateTime lastSave = DateTime.MinValue;

    public IReadOnlyList<HistoryEntry> Entries => entries;

    /// <summary>The house you're currently standing in (indoors or its yard), if any.</summary>
    public ulong? CurrentHouseId => haveBaseline ? baselineHouseId : null;

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
        haveStorageBaseline = false;
        tickStoreroomDelta.Clear();
        tickInventoryDelta.Clear();
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

        var houseId = (ulong)manager->GetCurrentHouseId();

        // Storeroom/inventory transfers never touch the furniture layout, so they don't bump
        // LastUpdate below, check every poll while settled in the same house/location we
        // already have a baseline for, or the early return right after would skip it entirely.
        if (haveBaseline && houseId == baselineHouseId && location == baselineLocation)
            UpdateStorageDeltas();

        // LastUpdate bumps ~every 200ms when furniture state changes. If it hasn't moved
        // since we last processed, there's nothing to diff, skip the snapshot build.
        var stamp = furnitureManager->LastUpdate;
        if (haveBaseline && houseId == baselineHouseId && location == baselineLocation && stamp == lastStamp)
        {
            // Furniture layout didn't change, so nothing below will consume the delta just
            // computed above, log a pure inventory/storeroom transfer directly if there is one.
            LogUnmatchedStorageTransfers(houseId, location.Value);
            return;
        }
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
            haveStorageBaseline = false; // different house/location now, storeroom contents reset
            tickStoreroomDelta.Clear();
            tickInventoryDelta.Clear();
            pendingDye.Clear();
            RememberLayout(houseId, location.Value, current);
            return;
        }

        DiffAndLog(baseline, current, houseId, location.Value, live: true);
        LogUnmatchedStorageTransfers(houseId, location.Value);
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
    {
        // A Removed/Stored item went somewhere (storeroom or inventory); a Placed item came
        // from somewhere. If exactly one real item unambiguously did that this same tick, its
        // confirmed name beats guessing a HousingFurniture sheet row, direct content diffing
        // has been reliable every time so far, the row guess hasn't.
        var grew = action is HistoryAction.Removed or HistoryAction.Stored;
        var resolved = action is HistoryAction.Placed or HistoryAction.Removed or HistoryAction.Stored
            ? TryResolveViaStorageDelta(grew)
            : null;
        var id = resolved?.itemId ?? DisplayId(r);
        var name = resolved?.name ?? NameResolver.Resolve(DisplayId(r));

        AddEntry(new HistoryEntry(DateTime.Now, action, index, id, name,
            r.Position, r.Rotation, null, 0f, r.Stain, r.Stain, houseId, Plugin.ClientState.TerritoryType, markAway, location,
            Quantity: 1, IsRawItemId: resolved != null));
    }

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

    // The raw HousingFurniture.Id the game briefly writes into a slot the instant an item is
    // removed or stored, before the slot is (sometimes) fully cleared to 0 on a later poll.
    // Confirmed indoors from a live log: removing "Mirific Mogshelf" logged a false Removed
    // for it immediately followed by a false Placed of "Furnishing #196608" at position
    // (0,0,0) in the exact same slot. 196608 is 0x30000, the indoor mask (0x20000) OR'd with
    // this raw id (0x10000). The same phantom entry showed up outdoors too, also displaying
    // 196608, but the outdoor mask is already 0x30000, so that number alone doesn't confirm
    // the raw id is the same 0x10000 out there (0x30000 | anything-already-in-that-mask
    // gives 196608 regardless). Kept as a cheap fast-path check for the confirmed indoor
    // case; IsOriginPlaceholder below is the real safety net since it doesn't depend on
    // guessing which raw id the game uses for "just vacated" in any given context.
    private const uint VacatedSlotId = 0x10000;

    // No item a decorator actually placed sits exactly on the plot's local origin with zero
    // rotation, indoors or in the yard, so this is a reliable "not a real item" signal
    // regardless of what raw id the game happens to be using for a slot mid-transition.
    private static bool IsOriginPlaceholder(Vector3 pos) => pos.LengthSquared() < LayoutDiffer.PositionEpsilon * LayoutDiffer.PositionEpsilon;

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
            if (f.Id == 0 || f.Id == VacatedSlotId)
                continue; // empty, or the game's own "just vacated" placeholder (see VacatedSlotId)

            var pos = new Vector3(f.Position.X, f.Position.Y, f.Position.Z);
            if (float.IsNaN(pos.X) || float.IsNaN(pos.Y) || float.IsNaN(pos.Z))
                return null; // garbage read (e.g. shifted offsets), signal a bad snapshot
            if (IsOriginPlaceholder(pos))
                continue; // same "just vacated" placeholder, under some other raw id (see IsOriginPlaceholder)

            map[i] = new FurnitureRecord(f.Id, ResolveRowId(furnitureManager, f.Id, f.Index, location), pos, f.Rotation, f.Stain);
        }

        return map;
    }

    /// <summary>
    /// The HousingFurniture sheet row for a placed object. Prefers reading it straight off the
    /// live game object's BaseId (per ReMakePlace), the same field indoor resolution relied on
    /// for months with zero wrong-name reports, only ever "unresolved" ones. Falls back to
    /// guessing the row from (0x20000|Id) indoors / (0x30000|Id) outdoors when the object isn't
    /// loaded, which is usually right but not guaranteed for every furniture category (a wall
    /// piece placed outdoors can be typed differently under the hood than a free-standing yard
    /// object, so the two masks can each be wrong for the other's category). An item you're
    /// actively watching move/store/remove is by definition rendered right now, so this should
    /// resolve correctly for exactly the cases that matter most; only far-off, unloaded items
    /// fall back to the location guess.
    /// </summary>
    private static unsafe uint ResolveRowId(HousingFurnitureManager* furnitureManager, uint rawId, int objIndex, HouseLocation location)
    {
        var objects = &furnitureManager->ObjectManager.ObjectArray;
        if (objIndex >= 0 && objIndex < objects->ObjectCount)
        {
            var gameObject = objects->Objects[objIndex].Value;
            if (gameObject != null && gameObject->BaseId != 0)
                return gameObject->BaseId;
        }

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

    private static readonly InventoryType[] RegularInventoryTypes =
        { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };

    /// <summary>
    /// Item-level companion to <see cref="StoreroomItemCount"/>: notices an item leaving the
    /// storeroom and landing in your regular inventory (or the reverse) in the same tick, which
    /// means it moved directly, never touching the room's furniture layout at all. That's
    /// invisible to everything else here, since Placed/Removed only fire on layout changes.
    /// Best-effort like the aggregate check: if a read lands mid-transfer it can miss one.
    /// </summary>
    /// <summary>
    /// Recomputes this tick's storeroom/inventory item deltas (what changed since the last
    /// poll), but doesn't log anything yet. Split from logging so a furniture Placed/Removed
    /// this same tick gets first claim on a matching delta (see TryResolveViaStorageDelta):
    /// content diffing has proven 100% reliable at naming an item correctly, unlike guessing
    /// its HousingFurniture sheet row, so it's worth using for room events too when there's an
    /// unambiguous match, not just for pure inventory/storeroom transfers.
    /// </summary>
    private unsafe void UpdateStorageDeltas()
    {
        tickStoreroomDelta.Clear();
        tickInventoryDelta.Clear();

        var curStore = SnapshotItemContents(27000, 27020);
        var curInv = SnapshotItemContents(RegularInventoryTypes);

        if (haveStorageBaseline)
        {
            var storeIds = new HashSet<uint>(prevStoreroomContents.Keys);
            storeIds.UnionWith(curStore.Keys);
            foreach (var itemId in storeIds)
            {
                var d = curStore.GetValueOrDefault(itemId) - prevStoreroomContents.GetValueOrDefault(itemId);
                if (d != 0)
                    tickStoreroomDelta[itemId] = d;
            }

            var invIds = new HashSet<uint>(prevInventoryContents.Keys);
            invIds.UnionWith(curInv.Keys);
            foreach (var itemId in invIds)
            {
                var d = curInv.GetValueOrDefault(itemId) - prevInventoryContents.GetValueOrDefault(itemId);
                if (d != 0)
                    tickInventoryDelta[itemId] = d;
            }
        }

        prevStoreroomContents = curStore;
        prevInventoryContents = curInv;
        haveStorageBaseline = true;
    }

    /// <summary>
    /// If exactly one real item unambiguously appeared in (or vanished from) the storeroom or
    /// your inventory this tick, use its confirmed real name/id instead of guessing a
    /// HousingFurniture row. "grew" picks an item that appeared (for a Removed/Stored event,
    /// wherever it ended up); false picks one that disappeared (for a Placed event, wherever
    /// it came from). Consumes the match so LogUnmatchedStorageTransfers doesn't also report it
    /// as a separate direct transfer. Returns null when there's no candidate, or more than one
    /// (genuinely ambiguous, e.g. several things changed in the same tick).
    /// </summary>
    private (uint itemId, string name)? TryResolveViaStorageDelta(bool grew)
    {
        var candidates = new HashSet<uint>();
        foreach (var (itemId, delta) in tickStoreroomDelta)
            if (grew ? delta > 0 : delta < 0)
                candidates.Add(itemId);
        foreach (var (itemId, delta) in tickInventoryDelta)
            if (grew ? delta > 0 : delta < 0)
                candidates.Add(itemId);

        if (candidates.Count != 1)
            return null;

        uint id = 0;
        foreach (var c in candidates) { id = c; break; }

        if (tickStoreroomDelta.TryGetValue(id, out var sd) && (grew ? sd > 0 : sd < 0))
            tickStoreroomDelta[id] = grew ? sd - 1 : sd + 1;
        if (tickInventoryDelta.TryGetValue(id, out var vd) && (grew ? vd > 0 : vd < 0))
            tickInventoryDelta[id] = grew ? vd - 1 : vd + 1;

        return (id, NameResolver.ResolveItemName(id));
    }

    /// <summary>
    /// Logs whatever's left in this tick's storeroom/inventory deltas as a direct Deposited or
    /// Withdrawn transfer, after DiffAndLog has already had first claim on anything that
    /// correlates with a furniture Placed/Removed/Stored event this same tick.
    /// </summary>
    private void LogUnmatchedStorageTransfers(ulong houseId, HouseLocation location)
    {
        foreach (var (itemId, storeDelta) in tickStoreroomDelta)
        {
            if (storeDelta == 0)
                continue;

            var invDelta = tickInventoryDelta.GetValueOrDefault(itemId);

            // A closed loop: the same item left one place and landed in the other, same tick.
            if (storeDelta > 0 && invDelta < 0)
            {
                var moved = Math.Min(storeDelta, -invDelta);
                if (moved > 0)
                    LogStorageTransfer(HistoryAction.Deposited, itemId, moved, houseId, location);
            }
            else if (storeDelta < 0 && invDelta > 0)
            {
                var moved = Math.Min(-storeDelta, invDelta);
                if (moved > 0)
                    LogStorageTransfer(HistoryAction.Withdrawn, itemId, moved, houseId, location);
            }
        }
    }

    private void LogStorageTransfer(HistoryAction action, uint itemId, int quantity, ulong houseId, HouseLocation location)
        => AddEntry(new HistoryEntry(DateTime.Now, action, -1, itemId, NameResolver.ResolveItemName(itemId),
            Vector3.Zero, 0f, null, 0f, 0, 0, houseId, Plugin.ClientState.TerritoryType, false, location,
            Quantity: quantity, IsRawItemId: true));

    private static unsafe Dictionary<uint, int> SnapshotItemContents(int typeMin, int typeMax)
    {
        var counts = new Dictionary<uint, int>();
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return counts;
        for (var t = typeMin; t <= typeMax; t++)
            AddContainerContents(mgr, (InventoryType)t, counts);
        return counts;
    }

    private static unsafe Dictionary<uint, int> SnapshotItemContents(InventoryType[] types)
    {
        var counts = new Dictionary<uint, int>();
        var mgr = InventoryManager.Instance();
        if (mgr == null)
            return counts;
        foreach (var t in types)
            AddContainerContents(mgr, t, counts);
        return counts;
    }

    private static unsafe void AddContainerContents(InventoryManager* mgr, InventoryType type, Dictionary<uint, int> counts)
    {
        var container = mgr->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded)
            return;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;
            counts[slot->ItemId] = counts.GetValueOrDefault(slot->ItemId) + slot->Quantity;
        }
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
            if (f.Id == 0 || f.Id == VacatedSlotId || ResolveRowId(furnitureManager, f.Id, f.Index, e.Location) != e.FurnitureId)
                continue;

            var pos = new Vector3(f.Position.X, f.Position.Y, f.Position.Z);
            if (IsOriginPlaceholder(pos))
                continue; // the "just vacated" placeholder, not a real item to select
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

                    var pos = new Vector3(f.Position.X, f.Position.Y, f.Position.Z);
                    var vacated = f.Id == VacatedSlotId || IsOriginPlaceholder(pos);
                    var rowId = ResolveRowId(furnitureManager, f.Id, f.Index, location.Value);
                    // Also show what the location-only guess would have said, so a mismatch
                    // here (only possible when the object IS loaded, since that's what makes
                    // rowId prefer BaseId over the guess) is visible directly in the dump.
                    var guessMask = location.Value == HouseLocation.Outdoor ? 0x30000u : 0x20000u;
                    var guessRowId = guessMask | f.Id;
                    var guessNote = guessRowId != rowId ? $" (location guess would say \"{NameResolver.Resolve(guessRowId)}\")" : "";
                    Plugin.Log.Information(
                        $"[dump] slot={i} rawId={f.Id}{(vacated ? " (vacated placeholder)" : "")} objIdx={f.Index} " +
                        $"obj={(gobj == null ? "null" : "ok")} rowId={rowId} name=\"{NameResolver.Resolve(rowId)}\"{guessNote}");
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
