using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;

namespace ARoomReborn;

/// <summary>
/// Optional write path (off by default): moves the item currently selected in the game's
/// housing layout (rotate) mode to a given position, the same mechanism Burning Down the
/// House uses. Guarded so it only ever writes when an item is actively selected for editing.
/// This is the only part of the plugin that writes to the game.
/// </summary>
internal static unsafe class HousingWriter
{
    // The game's own "select this housing item" function, so smart undo can pick the item for
    // you instead of you clicking it first. Same signature scan Burning Down the House uses. If
    // the scan misses (e.g. after a game patch shifts it), this stays null and smart undo
    // quietly falls back to the manual select-then-undo flow.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SelectItemDelegate(IntPtr housingStructure, IntPtr item);

    private static SelectItemDelegate? selectItem;

    /// <summary>Resolve the select-item function once at startup. Safe to call with a null scanner.</summary>
    public static void Init(ISigScanner? sigScanner)
    {
        if (sigScanner == null)
            return;
        try
        {
            var address = sigScanner.ScanText("48 85 D2 0F 84 ?? ?? ?? ?? 53 41 ?? 48 83 ?? ?? 48 89");
            selectItem = Marshal.GetDelegateForFunctionPointer<SelectItemDelegate>(address);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not find the housing select-item function; smart undo will fall back to manual selection.");
            selectItem = null;
        }
    }

    /// <summary>True when smart undo can select an item for you (the hook resolved and we're editing).</summary>
    public static bool CanSmartUndo()
    {
        var housing = Housing();
        return selectItem != null && housing != null && housing->Mode == HousingLayoutMode.Rotate;
    }

    /// <summary>
    /// Select the given item in the housing editor, then snap it back to a previous
    /// position/facing, all without the user having to click it first. Returns false if the
    /// hook is unavailable, we're not in edit mode, or the selection didn't take.
    /// </summary>
    public static bool TrySelectAndUndo(SharedGroupLayoutInstance* item, Vector3 position, float rotationRadians)
    {
        var housing = Housing();
        if (selectItem == null || housing == null || item == null || housing->Mode != HousingLayoutMode.Rotate)
            return false;

        selectItem((IntPtr)housing, (IntPtr)item);
        if (housing->ActiveItem != item)
            return false; // the game didn't take the selection; leave everything untouched

        housing->ActiveItem->Transform.Translation = position;
        housing->ActiveItem->Transform.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rotationRadians);
        return true;
    }

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
