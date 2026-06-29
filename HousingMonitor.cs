using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace HousingHistory;

/// <summary>
/// Polls the indoor furniture set and logs placements, removals, moves, and rotations
/// by diffing against the previous snapshot. Purely read-only — never writes to game memory.
/// </summary>
public sealed class HousingMonitor : IDisposable
{
    // A single placed object's state at snapshot time.
    private readonly record struct FurnitureRecord(uint Id, Vector3 Position, float Rotation, byte Stain);

    private const float PositionEpsilon = 0.01f; // yalms; ignore sub-threshold jitter
    private const float RotationEpsilon = 0.01f; // radians
    private const double MoveCoalesceSeconds = 5.0;
    private const double SaveDebounceSeconds = 15.0;

    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    private readonly Plugin plugin;
    private readonly List<HistoryEntry> entries = new();

    // Keyed by HousingFurniture.Index (a stable per-object id), so we can follow each
    // physical item's position across snapshots — and duplicate furnishings stay distinct.
    private Dictionary<int, FurnitureRecord> baseline = new();
    private bool haveBaseline;
    private ulong baselineHouseId;
    private long lastStamp = long.MinValue;
    private DateTime lastPoll = DateTime.MinValue;

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

    private void OnTerritoryChanged(ushort territory)
    {
        // Changing zones invalidates the baseline; it reseeds silently on the next read.
        haveBaseline = false;
        baseline.Clear();
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

        // Reseed silently on first read or when we've entered a different house
        // (two plots can share a TerritoryType, so we key off HouseId, not territory).
        if (!haveBaseline || houseId != baselineHouseId)
        {
            baseline = current;
            baselineHouseId = houseId;
            haveBaseline = true;
            Plugin.Log.Information($"Baseline seeded: {current.Count} item(s) (house {houseId:X}).");
            return;
        }

        DiffAndLog(baseline, current, houseId);
        baseline = current;
    }

    private void DiffAndLog(Dictionary<int, FurnitureRecord> oldSet, Dictionary<int, FurnitureRecord> newSet, ulong houseId)
    {
        foreach (var (index, now) in newSet)
        {
            if (!oldSet.TryGetValue(index, out var before))
            {
                LogSimple(HistoryAction.Placed, index, now, houseId);
            }
            else if (before.Id != now.Id)
            {
                // The game reused this object slot for a different furnishing.
                LogSimple(HistoryAction.Removed, index, before, houseId);
                LogSimple(HistoryAction.Placed, index, now, houseId);
            }
            else
            {
                var movedDistance = Vector3.DistanceSquared(before.Position, now.Position) > PositionEpsilon * PositionEpsilon;
                var rotated = MathF.Abs(before.Rotation - now.Rotation) > RotationEpsilon;

                if (movedDistance)
                    LogMovement(HistoryAction.Moved, index, before, now, houseId);
                else if (rotated)
                    LogMovement(HistoryAction.Rotated, index, before, now, houseId);
            }
        }

        foreach (var (index, before) in oldSet)
        {
            if (!newSet.ContainsKey(index))
                LogSimple(HistoryAction.Removed, index, before, houseId);
        }
    }

    private void LogSimple(HistoryAction action, int index, FurnitureRecord r, ulong houseId)
        => AddEntry(new HistoryEntry(DateTime.Now, action, index, r.Id, NameResolver.Resolve(r.Id),
            r.Position, r.Rotation, null, 0f, houseId, Plugin.ClientState.TerritoryType));

    private void LogMovement(HistoryAction action, int index, FurnitureRecord before, FurnitureRecord now, ulong houseId)
    {
        // Coalesce a drag/turn (many tiny updates) into one row: keep the original "from",
        // just refresh the "to". A rotate that becomes a move upgrades Rotated -> Moved.
        if (entries.Count > 0)
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

        AddEntry(new HistoryEntry(DateTime.Now, action, index, now.Id, NameResolver.Resolve(now.Id),
            now.Position, now.Rotation, before.Position, before.Rotation, houseId, Plugin.ClientState.TerritoryType));
    }

    private void AddEntry(HistoryEntry entry)
    {
        entries.Insert(0, entry);

        var max = Math.Max(10, plugin.Configuration.MaxEntries);
        if (entries.Count > max)
            entries.RemoveRange(max, entries.Count - max);

        dirty = true;
        Plugin.Log.Debug($"{entry.Action}: {entry.ItemName} (#{entry.FurnitureId}) @ {entry.Position}");
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

            map[f.Index] = new FurnitureRecord(f.Id, pos, f.Rotation, f.Stain);
        }

        return map;
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
                if (shown < 5)
                {
                    Plugin.Log.Information(
                        $"[dump]  idx={f.Index} id={f.Id} name=\"{NameResolver.Resolve(f.Id)}\" " +
                        $"pos=({f.Position.X:0.00}, {f.Position.Y:0.00}, {f.Position.Z:0.00})");
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
            if (!File.Exists(HistoryPath))
                return;

            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistoryPath), JsonOpts);
            if (loaded == null)
                return;

            entries.Clear();
            var max = Math.Max(10, plugin.Configuration.MaxEntries);
            entries.AddRange(loaded.Count > max ? loaded.GetRange(0, max) : loaded);
        }
        catch (Exception ex)
        {
            // Non-critical — a bad/old file just means we start with an empty log.
            Plugin.Log.Warning(ex, "Could not load saved history; starting fresh.");
        }
    }

    private void Save()
    {
        try
        {
            Plugin.PluginInterface.ConfigDirectory.Create();
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(entries, JsonOpts));
            dirty = false;
            lastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not save history.");
        }
    }
}
