using System;
using System.Collections.Generic;
using System.Numerics;

namespace HousingHistory;

/// <summary>A single detected difference between two furniture snapshots.</summary>
internal readonly record struct LayoutChange(HistoryAction Action, int Index, FurnitureRecord Before, FurnitureRecord After);

/// <summary>
/// Pure diff logic — no Dalamud or game dependencies, so it can be unit-tested in isolation.
/// Given two snapshots keyed by object index, produces the list of changes between them.
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

        foreach (var (index, now) in newSet)
        {
            if (!oldSet.TryGetValue(index, out var before))
            {
                changes.Add(new LayoutChange(HistoryAction.Placed, index, default, now));
            }
            else if (before.Id != now.Id)
            {
                // The game reused this object slot for a different furnishing.
                changes.Add(new LayoutChange(HistoryAction.Removed, index, before, default));
                changes.Add(new LayoutChange(HistoryAction.Placed, index, default, now));
            }
            else
            {
                var moved = Vector3.DistanceSquared(before.Position, now.Position) > PositionEpsilon * PositionEpsilon;
                var rotated = MathF.Abs(before.Rotation - now.Rotation) > RotationEpsilon;
                var redyed = before.Stain != now.Stain;

                if (moved)
                    changes.Add(new LayoutChange(HistoryAction.Moved, index, before, now));
                else if (rotated)
                    changes.Add(new LayoutChange(HistoryAction.Rotated, index, before, now));
                else if (redyed)
                    changes.Add(new LayoutChange(HistoryAction.Redyed, index, before, now));
            }
        }

        foreach (var (index, before) in oldSet)
        {
            if (!newSet.ContainsKey(index))
                changes.Add(new LayoutChange(HistoryAction.Removed, index, before, default));
        }

        return changes;
    }
}
