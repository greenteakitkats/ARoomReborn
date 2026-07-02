using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace HousingHistory.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 PlacedColor = new(0.40f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 RemovedColor = new(0.92f, 0.52f, 0.40f, 1f);
    private static readonly Vector4 MovedColor = new(0.45f, 0.70f, 0.95f, 1f);
    private static readonly Vector4 RotatedColor = new(0.90f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 DyedColor = new(0.82f, 0.58f, 0.88f, 1f);

    private readonly Plugin plugin;
    private string search = string.Empty;

    public MainWindow(Plugin plugin)
        : base("Housing History##HousingHistoryMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 320),
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

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        DrawFilter("Placed", cfg.ShowPlaced, v => { cfg.ShowPlaced = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Removed", cfg.ShowRemoved, v => { cfg.ShowRemoved = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Moved", cfg.ShowMoved, v => { cfg.ShowMoved = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Dyed", cfg.ShowDyed, v => { cfg.ShowDyed = v; cfg.Save(); });
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            plugin.Monitor.Clear();

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##search", "Search item…", ref search, 100);
        ImGui.SameLine();
        DrawFilter("Auto-open", cfg.AutoOpenWithHousing, v => { cfg.AutoOpenWithHousing = v; cfg.Save(); });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open this window automatically when the housing menu appears.");
        ImGui.SameLine();
        DrawFilter("New only", cfg.ShowOnlySinceLastOpen, v => { cfg.ShowOnlySinceLastOpen = v; cfg.Save(); });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only show changes since you last closed this window.");
        ImGui.SameLine();
        DrawFilter("Apply mode", cfg.EnableApplyToSelected, v => { cfg.EnableApplyToSelected = v; cfg.Save(); });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When on, clicking a coordinate MOVES the item selected in housing layout mode there (writes to the game, like BDTH). Off = copy to clipboard.");

        DrawTodaySummary();
        ImGui.Separator();

        using var table = ImRaii.Table("##history", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableHeadersRow();

        var any = false;
        var row = 0;
        foreach (var e in plugin.Monitor.Entries)
        {
            if (!IsVisible(e.Action, cfg))
                continue;
            if (search.Length > 0 && e.ItemName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (cfg.ShowOnlySinceLastOpen && e.Time <= cfg.SeenWatermark)
                continue;

            any = true;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Text(FormatWhen(e.Time));

            ImGui.TableNextColumn();
            DrawAction(e.Action);

            ImGui.TableNextColumn();
            var iconId = NameResolver.ResolveIcon(e.FurnitureId);
            if (iconId != 0)
            {
                var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
                ImGui.Image(tex.Handle, new Vector2(20, 20));
                ImGui.SameLine();
            }
            ImGui.Text(e.ItemName);
            if (e.Location == HouseLocation.Outdoor)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(yard)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This was outside in the yard, not indoors.");
            }
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
                DrawUndoButton(from, e.FromRotation, row);
            }
            else
            {
                CopyableCoord(Format(e.Position, e.Rotation), e.Position, false, row * 2);
            }

            row++;
        }

        if (!any)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled("No changes logged yet.");
        }
    }

    private static bool IsVisible(HistoryAction a, Configuration cfg) => a switch
    {
        HistoryAction.Placed => cfg.ShowPlaced,
        HistoryAction.Removed => cfg.ShowRemoved,
        HistoryAction.Moved => cfg.ShowMoved,
        HistoryAction.Rotated => cfg.ShowMoved, // rotations share the "Moved" filter
        HistoryAction.Redyed => cfg.ShowDyed,
        _ => true,
    };

    private static void DrawAction(HistoryAction a)
    {
        switch (a)
        {
            case HistoryAction.Placed: ImGui.TextColored(PlacedColor, "Placed"); break;
            case HistoryAction.Removed: ImGui.TextColored(RemovedColor, "Removed"); break;
            case HistoryAction.Moved: ImGui.TextColored(MovedColor, "Moved"); break;
            case HistoryAction.Rotated: ImGui.TextColored(RotatedColor, "Rotated"); break;
            case HistoryAction.Redyed: ImGui.TextColored(DyedColor, "Dyed"); break;
        }
    }

    private void CopyableCoord(string label, Vector3 pos, bool muted, int id)
    {
        if (muted)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));

        var clicked = ImGui.Selectable($"{label}##coord{id}");

        if (muted)
            ImGui.PopStyleColor();

        var applyMode = plugin.Configuration.EnableApplyToSelected;
        if (clicked)
        {
            if (applyMode)
                HousingWriter.TryApplyPosition(pos);
            else
                ImGui.SetClipboardText($"{pos.X:0.000} {pos.Y:0.000} {pos.Z:0.000}");
        }

        if (ImGui.IsItemHovered())
        {
            if (applyMode)
                ImGui.SetTooltip(HousingWriter.CanApply()
                    ? "Click to move the selected item here."
                    : "Apply mode: select an item in housing layout mode first.");
            else
                ImGui.SetTooltip("Click to copy X Y Z to clipboard.");
        }
    }

    private static void DrawUndoButton(Vector3 from, float fromRotation, int row)
    {
        if (ImGui.SmallButton($"Undo##undo{row}"))
            HousingWriter.TryUndo(from, fromRotation);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(HousingWriter.CanApply()
                ? "Snap the selected item back to where it was, position and facing."
                : "Select this item in housing layout mode first, then click to undo.");
    }

    private void DrawTodaySummary()
    {
        int placed = 0, removed = 0, moved = 0, dyed = 0;
        var today = DateTime.Now.Date;
        foreach (var e in plugin.Monitor.Entries)
        {
            if (e.Time.Date != today)
                continue;
            switch (e.Action)
            {
                case HistoryAction.Placed: placed++; break;
                case HistoryAction.Removed: removed++; break;
                case HistoryAction.Moved:
                case HistoryAction.Rotated: moved++; break;
                case HistoryAction.Redyed: dyed++; break;
            }
        }

        ImGui.TextDisabled($"Today: {placed} placed · {removed} removed · {moved} moved · {dyed} dyed");
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
