using Dalamud.Configuration;
using System;

namespace ARoomReborn;

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

    /// <summary>Deposited/Withdrawn: items moved straight between your inventory and the storeroom, never placed.</summary>
    public bool ShowStorageTransfers { get; set; } = true;

    /// <summary>Open the log automatically when the housing furnishing menu appears.</summary>
    public bool AutoOpenWithHousing { get; set; } = true;

    /// <summary>Only show entries newer than when the window was last closed.</summary>
    public bool ShowOnlySinceLastOpen { get; set; } = false;

    /// <summary>When the window was last closed, the watermark for "new only".</summary>
    public DateTime SeenWatermark { get; set; } = DateTime.MinValue;

    // Saving is exposed here just to keep call sites tidy.
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
