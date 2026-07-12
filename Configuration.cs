using Dalamud.Configuration;
using System;

namespace ARoomReborn;

public enum HouseScope { ThisHouse, AllHouses }
public enum LocationScope { Both, IndoorOnly, OutdoorOnly }

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>How many log entries to keep before the oldest are dropped.</summary>
    public int MaxEntries { get; set; } = 500;

    /// <summary>How often (seconds) to snapshot the room and diff it.</summary>
    public float PollIntervalSeconds { get; set; } = 1.0f;

    public bool ShowPlaced { get; set; } = true;
    public bool ShowRemoved { get; set; } = true;
    public bool ShowStored { get; set; } = true;
    public bool ShowMoved { get; set; } = true;
    public bool ShowDyed { get; set; } = true;

    /// <summary>Open the log automatically when the housing furnishing menu appears.</summary>
    public bool AutoOpenWithHousing { get; set; } = true;

    /// <summary>Show only the house you're currently in, or everything you've ever logged.</summary>
    public HouseScope HouseFilter { get; set; } = HouseScope.ThisHouse;

    /// <summary>Show indoor entries, outdoor (yard) entries, or both.</summary>
    public LocationScope LocationFilter { get; set; } = LocationScope.Both;

    /// <summary>Only show entries newer than when the window was last closed.</summary>
    public bool ShowOnlySinceLastOpen { get; set; } = false;

    /// <summary>When the window was last closed, the watermark for "new only".</summary>
    public DateTime SeenWatermark { get; set; } = DateTime.MinValue;

    /// <summary>
    /// When on, clicking a coordinate moves the item selected in housing layout mode there
    /// (writes to the game, like BDTH), instead of copying to the clipboard. Off by default.
    /// </summary>
    public bool EnableApplyToSelected { get; set; } = false;

    // Saving is exposed here just to keep call sites tidy.
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
