using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;

namespace ARoomReborn;

// The game's housing layout-editor mode. Items are editable in Rotate mode.
internal enum HousingLayoutMode
{
    None,
    Move,
    Rotate,
    Store,
    Place,
    Remove = 6,
}

// The housing layout editor structure (LayoutWorld + 0x40). Only the two fields we need.
// Offsets per Burning Down the House, which maintains them against the live client.
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct HousingStructure
{
    [FieldOffset(0x00)] public HousingLayoutMode Mode;
    [FieldOffset(0x18)] public SharedGroupLayoutInstance* ActiveItem;
}
