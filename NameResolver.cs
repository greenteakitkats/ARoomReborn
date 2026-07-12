using System.Collections.Generic;
using System.Numerics;
using Lumina.Excel.Sheets;

namespace ARoomReborn;

/// <summary>
/// Maps a placed-furniture id to a human-readable item name, with caching.
/// </summary>
/// <remarks>
/// VERIFY: <see cref="FFXIVClientStructs.FFXIV.Client.Game.HousingFurniture.Id"/> is documented as
/// "(0x20000 | Id) = HousingFurniture Row" indoors / "(0x30000 | Id)" outdoors. We first try the raw
/// id directly against the HousingFurniture sheet (correct for most indoor cases); if that misses we
/// fall back to showing the raw id so the log is still useful. If names come back as "Furnishing #N",
/// adjust the lookup key here.
/// </remarks>
public static class NameResolver
{
    private static readonly Dictionary<uint, string> Cache = new();
    private static readonly Dictionary<uint, uint> IconCache = new();
    private static readonly Dictionary<byte, string> StainCache = new();
    private static readonly Dictionary<byte, Vector4> StainColorCache = new();
    private static readonly Dictionary<uint, string> ItemNameCache = new();
    private static readonly Dictionary<uint, uint> ItemIconCache = new();

    /// <summary>Game icon id for a furnishing (0 if unknown).</summary>
    public static uint ResolveIcon(uint furnitureId)
    {
        if (IconCache.TryGetValue(furnitureId, out var cached))
            return cached;

        uint icon = 0;
        var sheet = Plugin.DataManager.GetExcelSheet<HousingFurniture>();
        if (sheet.TryGetRow(furnitureId, out var row) && row.Item.IsValid)
            icon = row.Item.Value.Icon;

        IconCache[furnitureId] = icon;
        return icon;
    }

    /// <summary>Approximate display color for a dye (transparent for "no dye").</summary>
    public static Vector4 ResolveStainColor(byte stainId)
    {
        if (stainId == 0)
            return new Vector4(0, 0, 0, 0);
        if (StainColorCache.TryGetValue(stainId, out var cached))
            return cached;

        var color = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var sheet = Plugin.DataManager.GetExcelSheet<Stain>();
        if (sheet.TryGetRow(stainId, out var row))
        {
            // Stain.Color is packed RGB in the low 24 bits. If swatches look channel-swapped
            // on your version, swap the R/B shifts here.
            var c = row.Color;
            color = new Vector4(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
        }

        StainColorCache[stainId] = color;
        return color;
    }

    public static string ResolveStain(byte stainId)
    {
        if (stainId == 0)
            return "No dye";
        if (StainCache.TryGetValue(stainId, out var cached))
            return cached;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Stain>();
        var name = sheet.TryGetRow(stainId, out var row) && !string.IsNullOrWhiteSpace(row.Name.ToString())
            ? row.Name.ToString()
            : $"Dye #{stainId}";

        StainCache[stainId] = name;
        return name;
    }

    /// <summary>
    /// Name for a raw inventory item id (not a HousingFurniture sheet row), used for
    /// storeroom/inventory transfers that never touch the room layout at all.
    /// </summary>
    public static string ResolveItemName(uint itemId)
    {
        if (ItemNameCache.TryGetValue(itemId, out var cached))
            return cached;

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        var name = sheet.TryGetRow(itemId, out var row) && !string.IsNullOrWhiteSpace(row.Name.ToString())
            ? row.Name.ToString()
            : $"Item #{itemId}";

        ItemNameCache[itemId] = name;
        return name;
    }

    /// <summary>Game icon id for a raw inventory item (0 if unknown).</summary>
    public static uint ResolveItemIcon(uint itemId)
    {
        if (ItemIconCache.TryGetValue(itemId, out var cached))
            return cached;

        uint icon = 0;
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (sheet.TryGetRow(itemId, out var row))
            icon = row.Icon;

        ItemIconCache[itemId] = icon;
        return icon;
    }

    public static string Resolve(uint furnitureId)
    {
        if (Cache.TryGetValue(furnitureId, out var cached))
            return cached;

        var name = Lookup(furnitureId);
        Cache[furnitureId] = name;
        return name;
    }

    private static string Lookup(uint furnitureId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<HousingFurniture>();
        if (sheet.TryGetRow(furnitureId, out var row) && row.Item.IsValid)
        {
            var itemName = row.Item.Value.Name.ToString();
            if (!string.IsNullOrWhiteSpace(itemName))
                return itemName;
        }

        return $"Furnishing #{furnitureId}";
    }
}
