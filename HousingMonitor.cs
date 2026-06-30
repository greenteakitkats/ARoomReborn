using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace HousingHistory;

/// <summary>
/// Polls the indoor furniture set and logs placements, removals, moves, rotations, and dye
/// changes by diffing against the previous snapshot. On entering a house it diffs against the
/// last-known layout to surface changes made while you were away. Purely read-only.
/// </summary>
public sealed class HousingMonitor : IDisposable
{
    private const double MoveCoalesceSeconds = 5.0;
    private const double SaveDebounceSeconds = 15.0;

    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    private readonly Plugin plugin;
    private readonly List<HistoryEntry> entries = new();

    // Last-known full layout per house (houseId -> index -> state). Persisted so we can
    // diff "what changed since last visit" — including changes made while you were away.
    private Dictionary<ulong, Dictionary<int, FurnitureRecord>> savedLayouts = new();

    // Live working state for the house we're currently inside.
    private Dictionary<int, FurnitureRecord> baseline = new();
    private bool haveBaseline;
    private ulong baselineHouseId;
    private long lastStamp = long.MinValue;
    private DateTime lastPoll = DateTime.MinValue;

    // Furniture streams in after a zone load — often reading empty or partial at first.
    // We ignore empty reads during a grace window and wait for the item count to hold
    // steady for several polls (and a minimum dwell) before trusting it as the baseline,
    // otherwise the load-in reads as a flood of placements every time you enter.
    private const int SettleReads = 3;
    private const double MinDwellSeconds = 5.0;
    private const double EmptyGraceSeconds = 15.0;
    private int settleCount;
    private int settleLastCount = -1;
    private ulong settleHouseId;
    private DateTime houseFirstSeen;

    // When true, entries created by the current diff are tagged as detected-on-entry.
    private bool markAway;

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
        settleLastCount = -1;
        settleCount = 0;
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
        if (manager == null || !manager->IsInside())
        {
            haveBaseline = false; // not in a readable house
            return;
        }

        var furnitureManager = manager->GetFurnitureManager();
        if (furnitureManager == null)
        {
            haveBaseline = false;
            return;
        }

        // LastUpdate bumps ~every 200ms when furniture state changes. If it hasn't moved
        // since we last processed, there's nothing to diff — skip the snapshot build.
        var stamp = furnitureManager->LastUpdate;
        var houseId = (ulong)manager->GetCurrentHouseId();
        if (haveBaseline && houseId == baselineHouseId && stamp == lastStamp)
            return;
        lastStamp = stamp;

        var current = BuildSnapshot(furnitureManager);
        if (current == null)
        {
            haveBaseline = false; // garbage read — wait for a clean one
            return;
        }

        // First read after entering, or after moving to a different house (two plots can
        // share a TerritoryType, so we key off HouseId, not territory).
        if (!haveBaseline || houseId != baselineHouseId)
        {
            // New house in view — start the settle/grace timers.
            if (settleHouseId != houseId)
            {
                settleHouseId = houseId;
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

            if (savedLayouts.TryGetValue(houseId, out var lastKnown))
            {
                // We've been here before — log only what's different since last visit.
                markAway = true;
                try { DiffAndLog(lastKnown, current, houseId); }
                finally { markAway = false; }
            }
            else
            {
                // First time we've ever seen this house — seed silently.
                Plugin.Log.Information($"First visit to house {houseId:X}: {current.Count} item(s).");
            }

            baseline = current;
            baselineHouseId = houseId;
            haveBaseline = true;
            settleHouseId = 0;
            settleLastCount = -1;
            settleCount = 0;
            RememberLayout(houseId, current);
            return;
        }

        DiffAndLog(baseline, current, houseId);
        baseline = current;
        RememberLayout(houseId, current);
    }

    private void RememberLayout(ulong houseId, Dictionary<int, FurnitureRecord> layout)
    {
        savedLayouts[houseId] = layout;
        dirty = true;
    }

    private void DiffAndLog(Dictionary<int, FurnitureRecord> oldSet, Dictionary<int, FurnitureRecord> newSet, ulong houseId)
    {
        foreach (var change in LayoutDiffer.Diff(oldSet, newSet))
        {
            switch (change.Action)
            {
                case HistoryAction.Placed:
                    LogSimple(HistoryAction.Placed, change.Index, change.After, houseId);
                    break;
                case HistoryAction.Removed:
                    LogSimple(HistoryAction.Removed, change.Index, change.Before, houseId);
                    break;
                case HistoryAction.Moved:
                case HistoryAction.Rotated:
                    LogMovement(change.Action, change.Index, change.Before, change.After, houseId);
                    break;
                case HistoryAction.Redyed:
                    LogRedyed(change.Index, change.Before, change.After, houseId);
                    break;
            }
        }
    }

    // Prefer the resolved sheet row for naming; fall back to the raw id so the log shows
    // a meaningful "#<rawId>" rather than "#0" if row resolution isn't working yet.
    private static uint DisplayId(FurnitureRecord r) => r.RowId != 0 ? r.RowId : r.Id;

    private void LogSimple(HistoryAction action, int index, FurnitureRecord r, ulong houseId)
        => AddEntry(new HistoryEntry(DateTime.Now, action, index, DisplayId(r), NameResolver.Resolve(DisplayId(r)),
            r.Position, r.Rotation, null, 0f, r.Stain, r.Stain, houseId, Plugin.ClientState.TerritoryType, markAway));

    private void LogRedyed(int index, FurnitureRecord before, FurnitureRecord now, ulong houseId)
        => AddEntry(new HistoryEntry(DateTime.Now, HistoryAction.Redyed, index, DisplayId(now), NameResolver.Resolve(DisplayId(now)),
            now.Position, now.Rotation, null, 0f, now.Stain, before.Stain, houseId, Plugin.ClientState.TerritoryType, markAway));

    private void LogMovement(HistoryAction action, int index, FurnitureRecord before, FurnitureRecord now, ulong houseId)
    {
        // Coalesce a live drag/turn (many tiny updates) into one row: keep the original "from",
        // just refresh the "to". A rotate that becomes a move upgrades Rotated -> Moved.
        // (Skipped for away-diffs, which produce at most one entry per object anyway.)
        if (!markAway && entries.Count > 0)
        {
            var top = entries[0];
            if (top.ObjectIndex == index
                && (top.Action == HistoryAction.Moved || top.Action == HistoryAction.Rotated)
                && (DateTime.Now - top.Time).TotalSeconds < MoveCoalesceSeconds)
            {
                var upgraded = top.Action == HistoryAction.Rotated && action == HistoryAction.Moved
                    ? HistoryAction.Moved
                    : top.Action;
                entries[0] = top with { Action = upgraded, Time = DateTime.Now, Position = now.Position, Rotation = now.Rotation };
                dirty = true;
                return;
            }
        }

        AddEntry(new HistoryEntry(DateTime.Now, action, index, DisplayId(now), NameResolver.Resolve(DisplayId(now)),
            now.Position, now.Rotation, before.Position, before.Rotation, now.Stain, before.Stain,
            houseId, Plugin.ClientState.TerritoryType, markAway));
    }

    private void AddEntry(HistoryEntry entry)
    {
        entries.Insert(0, entry);

        var max = Math.Max(10, plugin.Configuration.MaxEntries);
        if (entries.Count > max)
            entries.RemoveRange(max, entries.Count - max);

        dirty = true;
        Plugin.Log.Debug($"{entry.Action}{(entry.WhileAway ? " (away)" : "")}: {entry.ItemName} (#{entry.FurnitureId}) @ {entry.Position}");
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
                return null; // garbage read (e.g. shifted offsets) — signal a bad snapshot

            map[f.Index] = new FurnitureRecord(f.Id, ReadRowId(furnitureManager, f.Index), pos, f.Rotation, f.Stain);
        }

        return map;
    }

    /// <summary>
    /// The HousingFurniture sheet row for a placed object, read from the furniture game
    /// object (its GimmickId) via the object manager. This — not HousingFurniture.Id — is
    /// what maps to the item name. See MakePlace for the reference implementation.
    /// </summary>
    private static unsafe uint ReadRowId(HousingFurnitureManager* furnitureManager, int index)
    {
        var objects = &furnitureManager->ObjectManager.ObjectArray;
        if (index < 0 || index >= objects->ObjectCount)
            return 0;

        var gameObject = objects->Objects[index].Value;
        return gameObject != null ? gameObject->GimmickId : 0u;
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

            Plugin.Log.Information($"[dump] IsInside={manager->IsInside()} HouseId={(ulong)manager->GetCurrentHouseId():X}");

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
        }
        catch (Exception ex)
        {
            // Non-critical — a bad/old file just means we start with an empty log/layouts.
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
            dirty = false;
            lastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not save history.");
        }
    }
}
