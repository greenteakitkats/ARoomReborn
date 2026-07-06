using System.Numerics;

namespace ARoomReborn;

/// <summary>
/// A single placed object's state at snapshot time. <see cref="Id"/> is the raw encoded
/// id from the furniture array (used for identity); <see cref="RowId"/> is the resolved
/// HousingFurniture sheet row (used to look up the item name/icon).
/// </summary>
internal readonly record struct FurnitureRecord(uint Id, uint RowId, Vector3 Position, float Rotation, byte Stain);
