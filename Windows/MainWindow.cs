using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace HousingHistory.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 PlacedColor = new(0.40f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 RemovedColor = new(0.92f, 0.52f, 0.40f, 1f);
    private static readonly Vector4 MovedColor = new(0.45f, 0.70f, 0.95f, 1f);
    private static readonly Vector4 RotatedColor = new(0.90f, 0.75f, 0.35f, 1f);

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

    public override void Draw()
    {
        var cfg = plugin.Configuration;

        DrawFilter("Placed", cfg.ShowPlaced, v => { cfg.ShowPlaced = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Removed", cfg.ShowRemoved, v => { cfg.ShowRemoved = v; cfg.Save(); });
        ImGui.SameLine();
        DrawFilter("Moved", cfg.ShowMoved, v => { cfg.ShowMoved = v; cfg.Save(); });
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            plugin.Monitor.Clear();

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##search", "Search item…", ref search, 100);

        ImGui.Separator();

        using var table = ImRaii.Table("##history", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Position (X, Y, Z · rot)", ImGuiTableColumnFlags.WidthFixed, 200);
        ImGui.TableHeadersRow();

        var any = false;
        var row = 0;
        foreach (var e in plugin.Monitor.Entries)
        {
            if (!IsVisible(e.Action, cfg))
                continue;
            if (search.Length > 0 && e.ItemName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            any = true;
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Text(e.Time.ToString("HH:mm:ss"));

            ImGui.TableNextColumn();
            DrawAction(e.Action);

            ImGui.TableNextColumn();
            ImGui.Text(e.ItemName);

            ImGui.TableNextColumn();
            var isMove = e.Action is HistoryAction.Moved or HistoryAction.Rotated;
            if (isMove && e.FromPosition is { } from)
            {
                // Old (greyed) then new. Click either to copy "X Y Z" — the old line is the undo value.
                CopyableCoord(Format(from, e.FromRotation), from, true, row * 2);
                CopyableCoord("→ " + Format(e.Position, e.Rotation), e.Position, false, row * 2 + 1);
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
        }
    }

    private static void CopyableCoord(string label, Vector3 pos, bool muted, int id)
    {
        if (muted)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));

        var clicked = ImGui.Selectable($"{label}##coord{id}");

        if (muted)
            ImGui.PopStyleColor();

        if (clicked)
            ImGui.SetClipboardText($"{pos.X:0.000} {pos.Y:0.000} {pos.Z:0.000}");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Click to copy X Y Z to clipboard");
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
