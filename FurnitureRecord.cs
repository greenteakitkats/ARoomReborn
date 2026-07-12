using System.Numerics;

namespace ARoomReborn;

/// <summary>
/// A single placed object's state at snapshot time. <see cref="Id"/> is the raw id from
/// the furniture array; <see cref="RowId"/> is that id masked with 0x20000 (indoors) or
/// 0x30000 (outdoors) to get the actual HousingFurniture sheet row, used to look up the
/// item name/icon. <see cref="RowIdConfirmed"/> is true when RowId was read off the live
/// game object's BaseId (reliable) rather than guessed from the location mask — the guess
/// can be wrong outdoors, so display code uses this to avoid showing a confidently wrong
/// name. Old persisted layouts load it as false, which only affects labels, never diffing.
/// </summary>
internal readonly record struct FurnitureRecord(uint Id, uint RowId, Vector3 Position, float Rotation, byte Stain, bool RowIdConfirmed = false);
