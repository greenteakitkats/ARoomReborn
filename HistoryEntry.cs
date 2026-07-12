using System;
using System.Numerics;

namespace ARoomReborn;

public enum HistoryAction
{
    Placed,
    Removed,
    Moved,
    Rotated,
    Redyed,
    // Appended, not inserted: HistoryAction is serialized by numeric value in history.json,
    // so the existing values must keep their positions or old saved logs would be misread.
    Stored,    // removed straight into the housing storeroom rather than back to your inventory
    Deposited, // moved from your inventory into the storeroom directly, never placed
    Withdrawn, // moved from the storeroom into your inventory directly, never placed
}

public enum HouseLocation
{
    Indoor,
    Outdoor,
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
    uint TerritoryId,
    bool WhileAway,          // true if detected on entry, i.e. changed since your last visit
    HouseLocation Location = HouseLocation.Indoor,
    int Quantity = 1);       // Deposited/Withdrawn only: how many of the item moved
