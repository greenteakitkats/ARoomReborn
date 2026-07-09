using System;
using System.Collections.Generic;
using System.Numerics;

namespace ARoomReborn;

/// <summary>A single detected difference between two furniture snapshots.</summary>
internal readonly record struct LayoutChange(HistoryAction Action, int Index, FurnitureRecord Before, FurnitureRecord After);

/// <summary>
/// Pure diff logic, no Dalamud or game dependencies, so it can be unit-tested in isolation.
/// Given two snapshots keyed by storage slot, produces the list of changes between them.
/// </summary>
internal static class LayoutDiffer
{
    public const float PositionEpsilon = 0.01f; // yalms; ignore sub-threshold jitter
    public const float RotationEpsilon = 0.01f; // radians

    public static List<LayoutChange> Diff(
        IReadOnlyDictionary<int, FurnitureRecord> oldSet,
        IReadOnlyDictionary<int, FurnitureRecord> newSet)
    {
        var changes = new List<LayoutChange>();
        var removed = new List<(int Index, FurnitureRecord Record)>();
        var placed = new List<(int Index, FurnitureRecord Record)>();

        foreach (var (index, now) in newSet)
        {
            if (!oldSet.TryGetValue(index, out var before))
            {
                placed.Add((index, now));
            }
            else if (before.Id != now.Id)
            {
                // A different furnishing now occupies this slot. Could be a genuine swap, or
                // the same object landing back in some slot after being reindexed elsewhere:
                // outdoor items aren't guaranteed a stable slot the way indoor's are. The
                // reconciliation pass below tells the two apart.
                removed.Add((index, before));
                placed.Add((index, now));
            }
            else
            {
                var action = Classify(before, now);
                if (action != null)
                    changes.Add(new LayoutChange(action.Value, index, before, now));
            }
        }

        foreach (var (index, before) in oldSet)
        {
            if (!newSet.ContainsKey(index))
                removed.Add((index, before));
        }

        Reconcile(removed, placed, changes);
        return changes;
    }

    /// <summary>What changed between two states of the same object, or null if nothing did.</summary>
    private static HistoryAction? Classify(FurnitureRecord before, FurnitureRecord now)
    {
        var moved = Vector3.DistanceSquared(before.Position, now.Position) > PositionEpsilon * PositionEpsilon;
        var rotated = MathF.Abs(before.Rotation - now.Rotation) > RotationEpsilon;
        var redyed = before.Stain != now.Stain;

        if (moved) return HistoryAction.Moved;
        if (rotated) return HistoryAction.Rotated;
        if (redyed) return HistoryAction.Redyed;
        return null;
    }

    /// <summary>
    /// An object's slot isn't guaranteed stable outdoors, so the same physical item can vanish
    /// from one index and reappear at another with nothing actually changed (walking around
    /// changes what the game keeps loaded). Rather than trust a raw index mismatch as a real
    /// removal or placement, first try to pair it with a same-id counterpart on the other
    /// side: an exact match is that same non-event and gets dropped silently; a real
    /// difference is a genuine move (or dye change) that happened to change slots too.
    /// </summary>
    private static void Reconcile(
        List<(int Index, FurnitureRecord Record)> removed,
        List<(int Index, FurnitureRecord Record)> placed,
        List<LayoutChange> changes)
    {
        for (var i = removed.Count - 1; i >= 0; i--)
        {
            var r = removed[i];
            var j = placed.FindIndex(p => p.Record.Id == r.Record.Id && Classify(r.Record, p.Record) == null);
            if (j < 0)
                continue;

            removed.RemoveAt(i);
            placed.RemoveAt(j);
        }

        for (var i = removed.Count - 1; i >= 0; i--)
        {
            var r = removed[i];
            var j = placed.FindIndex(p => p.Record.Id == r.Record.Id);
            if (j < 0)
                continue;

            var p = placed[j];
            changes.Add(new LayoutChange(Classify(r.Record, p.Record) ?? HistoryAction.Moved, p.Index, r.Record, p.Record));
            removed.RemoveAt(i);
            placed.RemoveAt(j);
        }

        foreach (var (index, record) in removed)
            changes.Add(new LayoutChange(HistoryAction.Removed, index, record, default));
        foreach (var (index, record) in placed)
            changes.Add(new LayoutChange(HistoryAction.Placed, index, default, record));
    }
}
