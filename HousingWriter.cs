using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace ARoomReborn;

/// <summary>
/// Optional write path (off by default): moves the item currently selected in the game's
/// housing layout (rotate) mode to a given position, the same mechanism Burning Down the
/// House uses. Guarded so it only ever writes when an item is actively selected for editing.
/// This is the only part of the plugin that writes to the game.
/// </summary>
internal static unsafe class HousingWriter
{
    private static HousingStructure* Housing()
    {
        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
            return null;

        // LayoutWorld + 0x40 holds the housing layout editor structure (per BDTH).
        return *(HousingStructure**)((byte*)layoutWorld + 0x40);
    }

    /// <summary>True when an item is selected for editing (rotate mode), so a write is safe.</summary>
    public static bool CanApply()
    {
        var housing = Housing();
        return housing != null && housing->Mode == HousingLayoutMode.Rotate && housing->ActiveItem != null;
    }

    /// <summary>Move the selected item to <paramref name="position"/>. Returns false if nothing is selected.</summary>
    public static bool TryApplyPosition(Vector3 position)
    {
        var housing = Housing();
        if (housing == null || housing->Mode != HousingLayoutMode.Rotate || housing->ActiveItem == null)
            return false;

        housing->ActiveItem->Transform.Translation = position;
        return true;
    }

    /// <summary>
    /// Move and turn the selected item back to a previous position/rotation in one step, for
    /// the undo button. Housing items only ever yaw around the vertical axis, so the single
    /// rotation float maps straight onto a Y-axis quaternion (matches Burning Down the House's
    /// conversion for a pure-yaw angle).
    /// </summary>
    public static bool TryUndo(Vector3 position, float rotationRadians)
    {
        var housing = Housing();
        if (housing == null || housing->Mode != HousingLayoutMode.Rotate || housing->ActiveItem == null)
            return false;

        housing->ActiveItem->Transform.Translation = position;
        housing->ActiveItem->Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rotationRadians);
        return true;
    }
}
