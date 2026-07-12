using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using ARoomReborn.Windows;

namespace ARoomReborn;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/houselog";

    // Windows that mean the player is decorating. Opening any of these opens the log:
    // the furnishing catalogue, the layout toolbar, and the two dye windows.
    private static readonly string[] OpenAddons = { "HousingGoods", "HousingLayout", "HousingGoodsStain", "ColorantColoring" };

    // Closing the main housing menus closes the log. The dye windows are left out, since
    // you're usually still in layout mode after picking a color.
    private static readonly string[] CloseAddons = { "HousingGoods", "HousingLayout" };

    public Configuration Configuration { get; init; }
    public HousingMonitor Monitor { get; init; }

    public readonly WindowSystem WindowSystem = new("ARoomReborn");
    private MainWindow MainWindow { get; init; }

    private bool addonLogging;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        HousingWriter.Init(SigScanner);

        Monitor = new HousingMonitor(this);

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the housing edit-history log. \"/houselog dump\" logs a diagnostics snapshot; \"/houselog addons\" logs addon names (to identify a window).",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OpenAddons, OnHousingAddon);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, CloseAddons, OnHousingAddonClose);

        Log.Information("A Room Reborn loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        AddonLifecycle.UnregisterListener(OnHousingAddon);
        AddonLifecycle.UnregisterListener(OnHousingAddonClose);
        if (addonLogging)
            AddonLifecycle.UnregisterListener(OnAnyAddonSetup);

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        Monitor.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "dump":
                Monitor.LogDiagnostics();
                return;
            case "addons":
                ToggleAddonLogging();
                return;
            default:
                MainWindow.Toggle();
                return;
        }
    }

    private void ToggleAddonLogging()
    {
        addonLogging = !addonLogging;
        if (addonLogging)
        {
            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnAnyAddonSetup);
            Log.Information("[addons] Logging on. Open the dye window, note the names in /xllog, then run /houselog addons again to stop.");
        }
        else
        {
            AddonLifecycle.UnregisterListener(OnAnyAddonSetup);
            Log.Information("[addons] Logging off.");
        }
    }

    private void OnAnyAddonSetup(AddonEvent type, AddonArgs args)
        => Log.Information($"[addons] opened: {args.AddonName}");

    private void ToggleMainUi() => MainWindow.Toggle();

    private void OnHousingAddon(AddonEvent type, AddonArgs args)
    {
        // Guard on being indoors so dyeing gear in town doesn't pop the window open, and so
        // opening the yard layout toolbar doesn't either — outdoor tracking is disabled, and
        // auto-opening onto an empty/irrelevant log there is just noise.
        if (Configuration.AutoOpenWithHousing && InHouseIndoors())
            MainWindow.IsOpen = true;
    }

    private static unsafe bool InHouseIndoors()
    {
        var manager = HousingManager.Instance();
        return manager != null && manager->IsInside();
    }

    private void OnHousingAddonClose(AddonEvent type, AddonArgs args)
    {
        if (Configuration.AutoOpenWithHousing)
            MainWindow.IsOpen = false;
    }
}
