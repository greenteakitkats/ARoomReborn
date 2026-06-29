using System;
using System.Numerics;

namespace HousingHistory;

public enum HistoryAction
{
    Placed,
    Removed,
    Moved,
    Rotated,
    Redyed,
}

/// <summary>One row in the edit-history log.</summary>
public readonly record struct HistoryEntry(
    DateTime Time,
    HistoryAction Action,
    int ObjectIndex,
    uint FurnitureId,
    string ItemName,
    Vector3 Position,        // Placed/Removed: the item's location. Moved/Rotated: the NEW location.
    float Rotation,          // radians. Moved/Rotated: the NEW rotation.
    Vector3? FromPosition,   // Moved/Rotated only: the previous location.
    float FromRotation,      // Moved/Rotated only: the previous rotation (radians).
    byte Stain,              // current dye id. Redyed: the NEW dye.
    byte FromStain,          // Redyed only: the previous dye id.
    ulong HouseId,           // which house this happened in (for multi-house disambiguation)
    ushort TerritoryId,
    bool WhileAway);         // true if detected on entry — i.e. changed since your last visit
