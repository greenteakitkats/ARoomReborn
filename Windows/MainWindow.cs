using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace ARoomReborn.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 PlacedColor = new(0.40f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 RemovedColor = new(0.92f, 0.52f, 0.40f, 1f);
    private static readonly Vector4 StoredColor = new(0.55f, 0.72f, 0.74f, 1f);
    private static readonly Vector4 MovedColor = new(0.45f, 0.70f, 0.95f, 1f);
    private static readonly Vector4 RotatedColor = new(0.90f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 DyedColor = new(0.82f, 0.58f, 0.88f, 1f);
    private static readonly Vector4 DepositedColor = new(0.50f, 0.78f, 0.65f, 1f);
    private static readonly Vector4 WithdrawnColor = new(0.78f, 0.65f, 0.45f, 1f);

    private readonly Plugin plugin;
    private string search = string.Empty;

    public MainWindow(Plugin plugin)
        : base("A Room Reborn##ARoomRebornMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(680, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void OnClose()
    {
        // Watermark for the "new only" filter, anything after this is "since last open".
        plugin.Configuration.SeenWatermark = DateTime.Now;
        plugin.Configuration.Save();
    }

    private static readonly Vector4 NoticeColor = new(0.90f, 0.75f, 0.35f, 1f);

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        if (HousingMonitor.IsCurrentlyOutside())
        {
            ImGui.TextColored(NoticeColor, "Outdoor/yard tracking is currently disabled — this only tracks indoor changes for now.");
            ImGui.Separator();
        }

        // Row 1: what to show, plus a way to search and reset. These change often, so they're
        // the first thing you see and touch.
        DrawFilter("Placed", cfg.ShowPlaced, v => { cfg.ShowPlaced = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Removed", cfg.ShowRemoved, v => { cfg.ShowRemoved = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Stored", cfg.ShowStored, v => { cfg.ShowStored = v; cfg.Save(); });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Items removed straight into the housing storeroom, as opposed to sent to your inventory.");
        ImGui.SameLine();
        DrawFilter("Moved", cfg.ShowMoved, v => { cfg.ShowMoved = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Dyed", cfg.ShowDyed, v => { cfg.ShowDyed = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Storage", cfg.ShowStorageTransfers, v => { cfg.ShowStorageTransfers = v; cfg.Save(); });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Items moved directly between your inventory and the storeroom, never placed in the room at all.");
        ImGui.SameLine();
        DrawFilter("New only", cfg.ShowOnlySinceLastOpen, v => { cfg.ShowOnlySinceLastOpen = v; cfg.Save(); });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only show changes since you last closed this window.");

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##search", "Search item…", ref search, 100);
        ImGui.SameLine();
        if (ImGui.Button("Clear log"))
            ImGui.OpenPopup("ConfirmClear");
        DrawClearConfirm();

        // Row 2: settings you set once and forget, kept visually separate from the filters
        // above so they don't compete for attention every time you open the window. Just one
        // now: clicking a coordinate always moves the selected item (Undo already covers
        // recovering from a mistake, so there's no separate "safe" mode to opt out of).
        DrawFilter("Auto-open", cfg.AutoOpenWithHousing, v => { cfg.AutoOpenWithHousing = v; cfg.Save(); });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open this window automatically when the housing menu appears.");

        DrawTodaySummary();
        ImGui.Separator();

        // Always scoped to the house AND location (indoor/yard) you're currently in (or last
        // visited, if you're not in one right now), so indoor and outdoor histories read as
        // fully separate logs, never mixed on the same screen.
        var effectiveHouseId = plugin.Monitor.CurrentHouseId
            ?? (plugin.Monitor.Entries.Count > 0 ? plugin.Monitor.Entries[0].HouseId : (ulong?)null);
        var effectiveLocation = plugin.Monitor.CurrentHouseId != null
            ? plugin.Monitor.CurrentLocation
            : (plugin.Monitor.Entries.Count > 0 ? plugin.Monitor.Entries[0].Location : (HouseLocation?)null);

        var any = false;
        using (var table = ImRaii.Table("##history", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 74);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 1.3f);
                ImGui.TableHeadersRow();

                var row = 0;
                foreach (var e in plugin.Monitor.Entries)
                {
                    if (!IsVisible(e.Action, cfg))
                        continue;
                    if (search.Length > 0 && e.ItemName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (cfg.ShowOnlySinceLastOpen && e.Time <= cfg.SeenWatermark)
                        continue;
                    if (effectiveHouseId is { } scopedHouse && e.HouseId != scopedHouse)
                        continue;
                    if (effectiveLocation is { } scopedLocation && e.Location != scopedLocation)
                        continue;

                    any = true;
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text(FormatWhen(e.Time));

                    ImGui.TableNextColumn();
                    DrawAction(e.Action);

                    ImGui.TableNextColumn();
                    var isStorageTransfer = e.Action is HistoryAction.Deposited or HistoryAction.Withdrawn;
                    // IsRawItemId means FurnitureId is a real inventory item id (confirmed via
                    // content diffing, always true for Deposited/Withdrawn, sometimes true for
                    // Placed/Removed/Stored too), not a guessed HousingFurniture sheet row, so
                    // it needs the other name resolver.
                    var iconId = e.IsRawItemId ? NameResolver.ResolveItemIcon(e.FurnitureId) : NameResolver.ResolveIcon(e.FurnitureId);
                    if (iconId != 0)
                    {
                        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
                        ImGui.Image(tex.Handle, new Vector2(20, 20));
                        ImGui.SameLine();
                    }
                    ImGui.Text(e.ItemName);
                    if (e.WhileAway)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled("(away)");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("This changed while you were away. Spotted when you walked back in.");
                    }

                    ImGui.TableNextColumn();
                    var isMove = e.Action is HistoryAction.Moved or HistoryAction.Rotated;
                    if (e.Action == HistoryAction.Redyed)
                    {
                        DrawDyeSwatch(e.FromStain, row * 2);
                        ImGui.SameLine();
                        ImGui.TextDisabled(NameResolver.ResolveStain(e.FromStain));
                        ImGui.SameLine();
                        ImGui.Text("→");
                        ImGui.SameLine();
                        DrawDyeSwatch(e.Stain, row * 2 + 1);
                        ImGui.SameLine();
                        ImGui.Text(NameResolver.ResolveStain(e.Stain));
                    }
                    else if (isMove && e.FromPosition is { } from)
                    {
                        // Old (greyed) then new. Click either to copy "X Y Z", the old line is the undo value.
                        CopyableCoord(Format(from, e.FromRotation), from, true, row * 2);
                        CopyableCoord("→ " + Format(e.Position, e.Rotation), e.Position, false, row * 2 + 1);
                        DrawUndoButton(e, from, e.FromRotation, row);
                    }
                    else if (isStorageTransfer)
                    {
                        // No coordinates for these, they never touched the room. Only worth
                        // calling out the quantity when it's more than one of the item.
                        var direction = e.Action == HistoryAction.Deposited ? "from your inventory" : "to your inventory";
                        ImGui.TextDisabled(e.Quantity > 1 ? $"×{e.Quantity} {direction}" : direction);
                    }
                    else
                    {
                        CopyableCoord(Format(e.Position, e.Rotation), e.Position, false, row * 2);
                    }

                    row++;
                }
            }
        }

        // Drawn below the table, not inside a table cell, so the message isn't clipped to a
        // single column's width. Also says *why* the log looks empty when it isn't really
        // empty (a filter or "New only" is hiding real entries), since that's confusing
        // otherwise: the summary line above can say "500 placed" while the table shows nothing.
        if (!any)
            DrawEmptyState(cfg);
    }

    private void DrawEmptyState(Configuration cfg)
    {
        ImGui.Spacing();
        var total = plugin.Monitor.Entries.Count;

        if (total == 0)
        {
            ImGui.TextDisabled("Nothing logged yet.");
            ImGui.TextDisabled("Place, move, or dye something in housing layout mode to get started.");
            return;
        }

        if (search.Length > 0)
        {
            ImGui.TextDisabled($"No items match \"{search}\".");
            return;
        }

        var reason = cfg.ShowOnlySinceLastOpen ? "hidden because \"New only\" is on"
            : "hidden by your action filters, or belong to a different house or location (indoor/yard)";
        ImGui.TextDisabled($"{total} change{(total == 1 ? "" : "s")} logged, but {reason}.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Show everything"))
        {
            cfg.ShowPlaced = cfg.ShowRemoved = cfg.ShowStored = cfg.ShowMoved = cfg.ShowDyed = cfg.ShowStorageTransfers = true;
            cfg.ShowOnlySinceLastOpen = false;
            cfg.Save();
        }
    }

    private static bool IsVisible(HistoryAction a, Configuration cfg) => a switch
    {
        HistoryAction.Placed => cfg.ShowPlaced,
        HistoryAction.Removed => cfg.ShowRemoved,
        HistoryAction.Stored => cfg.ShowStored,
        HistoryAction.Moved => cfg.ShowMoved,
        HistoryAction.Rotated => cfg.ShowMoved, // rotations share the "Moved" filter
        HistoryAction.Redyed => cfg.ShowDyed,
        HistoryAction.Deposited => cfg.ShowStorageTransfers,
        HistoryAction.Withdrawn => cfg.ShowStorageTransfers,
        _ => true,
    };

    // "Clear log" wipes history with no undo, so it gets a confirm step instead of firing on
    // the first click, the same way it sits right next to filter checkboxes people click a lot.
    private void DrawClearConfirm()
    {
        if (!ImGui.BeginPopup("ConfirmClear"))
            return;

        ImGui.Text("Clear the whole log? This can't be undone.");
        ImGui.Spacing();
        if (ImGui.Button("Clear it"))
        {
            plugin.Monitor.Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    private static void DrawAction(HistoryAction a)
    {
        switch (a)
        {
            case HistoryAction.Placed: ImGui.TextColored(PlacedColor, "Placed"); break;
            case HistoryAction.Removed: ImGui.TextColored(RemovedColor, "Removed"); break;
            case HistoryAction.Stored: ImGui.TextColored(StoredColor, "Stored"); break;
            case HistoryAction.Moved: ImGui.TextColored(MovedColor, "Moved"); break;
            case HistoryAction.Rotated: ImGui.TextColored(RotatedColor, "Rotated"); break;
            case HistoryAction.Redyed: ImGui.TextColored(DyedColor, "Dyed"); break;
            case HistoryAction.Deposited: ImGui.TextColored(DepositedColor, "Deposited"); break;
            case HistoryAction.Withdrawn: ImGui.TextColored(WithdrawnColor, "Withdrawn"); break;
        }
    }

    private void CopyableCoord(string label, Vector3 pos, bool muted, int id)
    {
        if (muted)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));

        var clicked = ImGui.Selectable($"{label}##coord{id}");

        if (muted)
            ImGui.PopStyleColor();

        // Clicking always moves the item selected in housing layout mode there, matching BDTH.
        // No-op (and the tooltip says why) if nothing's selected, so there's nothing to opt out
        // of, either it does something useful or it quietly does nothing.
        if (clicked)
            HousingWriter.TryApplyPosition(pos);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(HousingWriter.CanApply()
                ? "Click to move the selected item here."
                : "Select an item in housing layout mode first, then click to move it here.");
    }

    private void DrawUndoButton(HistoryEntry e, Vector3 from, float fromRotation, int row)
    {
        if (ImGui.SmallButton($"Undo##undo{row}"))
        {
            // Try the one-click path first: it finds and selects the item for you, then snaps
            // it back. If that can't run (item moved again, out of range, or the select hook
            // isn't available), fall back to acting on whatever you already have selected.
            if (!plugin.Monitor.TrySmartUndo(e))
                HousingWriter.TryUndo(from, fromRotation);
        }

        if (ImGui.IsItemHovered())
        {
            if (HousingWriter.CanSmartUndo())
                ImGui.SetTooltip("Click to select this item and snap it back, position and facing.");
            else if (HousingWriter.CanApply())
                ImGui.SetTooltip("Snap the selected item back to where it was, position and facing.");
            else
                ImGui.SetTooltip("Enter housing layout mode, then click to undo (it picks the item for you).");
        }
    }

    private void DrawTodaySummary()
    {
        int placed = 0, removed = 0, stored = 0, moved = 0, dyed = 0, storageTransfers = 0;
        var today = DateTime.Now.Date;
        foreach (var e in plugin.Monitor.Entries)
        {
            if (e.Time.Date != today)
                continue;
            switch (e.Action)
            {
                case HistoryAction.Placed: placed++; break;
                case HistoryAction.Removed: removed++; break;
                case HistoryAction.Stored: stored++; break;
                case HistoryAction.Moved:
                case HistoryAction.Rotated: moved++; break;
                case HistoryAction.Redyed: dyed++; break;
                case HistoryAction.Deposited:
                case HistoryAction.Withdrawn: storageTransfers++; break;
            }
        }

        // Only show tallies once there's something to show, to keep the line short on a normal day.
        var storedPart = stored > 0 ? $" · {stored} stored" : "";
        var storagePart = storageTransfers > 0 ? $" · {storageTransfers} storage" : "";
        ImGui.TextDisabled($"Today: {placed} placed · {removed} removed{storedPart} · {moved} moved · {dyed} dyed{storagePart}");
    }

    private static void DrawDyeSwatch(byte stainId, int id)
    {
        if (stainId == 0)
        {
            ImGui.Dummy(new Vector2(14, 14));
            return;
        }

        ImGui.ColorButton($"##dye{id}", NameResolver.ResolveStainColor(stainId),
            ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoPicker | ImGuiColorEditFlags.NoTooltip,
            new Vector2(14, 14));
    }

    private static string FormatWhen(DateTime t)
    {
        // Culture-aware: 12h AM/PM or 24h follows the player's OS locale. Show the date too
        // when it isn't today, since houses aren't edited every day.
        var c = CultureInfo.CurrentCulture;
        return t.Date == DateTime.Now.Date
            ? t.ToString("t", c)
            : $"{t.ToString("d", c)} {t.ToString("t", c)}";
    }

    private static string Format(Vector3 p, float rotationRadians)
    {
        var deg = rotationRadians * 180f / MathF.PI;
        return $"{p.X:0.00}, {p.Y:0.00}, {p.Z:0.00} · {deg:0}°";
    }

    private static void DrawFilter(string label, bool value, Action<bool> set)
    {
        var v = value;
        if (ImGui.Checkbox(label, ref v))
            set(v);
    }
}
