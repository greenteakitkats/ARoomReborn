using System.Numerics;

namespace HousingHistory;

/// <summary>A single placed object's state at snapshot time.</summary>
internal readonly record struct FurnitureRecord(uint Id, Vector3 Position, float Rotation, byte Stain);
