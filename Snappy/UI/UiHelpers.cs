using Luna;

namespace Snappy.UI;

public static class UiHelpers
{
    public static bool DrawStretchedIconButtonWithText(
        FontAwesomeIcon icon,
        string text,
        string tooltip,
        bool disabled = false,
        float? fixedWidth = null
    )
    {
        var textSize = Im.Font.CalculateSize(text);
        var framePadding = ImGui.GetStyle().FramePadding;

        var contentMaxHeight = Math.Max(icon.CalculateSize().Y, textSize.Y);
        var buttonHeight =
            contentMaxHeight + framePadding.Y * 2 + 8f * ImGuiHelpers.GlobalScale;
        var buttonWidth = fixedWidth ?? ImGui.GetContentRegionAvail().X;
        var config = new ImEx.ButtonConfiguration
        {
            Size = new Vector2(buttonWidth, buttonHeight),
            Disabled = disabled,
        };

        return ImEx.Icon.LabeledButton(icon.Icon(), text, tooltip, in config);
    }

    public static void DrawInlineRename(string id, ref string text, Action onCommit, Action onCancel)
    {
        var iconButtonSize = ImGui.GetFrameHeight();
        var buttonsWidth = iconButtonSize * 2 + ImGui.GetStyle().ItemSpacing.X;
        var inputWidth = ImGui.GetContentRegionAvail().X - buttonsWidth - ImGui.GetStyle().ItemSpacing.X;

        ImGui.SetNextItemWidth(inputWidth);
        using (var color = ImRaii.PushColor(ImGuiCol.Border, new Vector4(1, 1, 0, 0.5f)))
        {
            if (Im.Input.Text(
                    "##" + id,
                    ref text,
                    flags: InputTextFlags.EnterReturnsTrue | InputTextFlags.AutoSelectAll
                ))
                onCommit();
        }

        ImGui.SameLine();
        if (IconButton(FontAwesomeIcon.Check)) onCommit();

        ImGui.SameLine();
        if (IconButton(FontAwesomeIcon.Times)) onCancel();
    }

    public static bool ButtonEx(string label, string tooltip, Vector2 size, bool disabled = false)
    {
        using var _ = ImRaii.Disabled(disabled);
        var ret = Im.Button(label, size);
        Im.Tooltip.OnHover(HoveredFlags.AllowWhenDisabled, tooltip);
        return ret && !disabled;
    }

    public static bool IconButton(FontAwesomeIcon icon, string tooltip = "", Vector2 size = default, bool disabled = false)
    {
        var actualSize = size == default ? new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()) : size;
        bool ret;
        using (var disabledScope = ImRaii.Disabled(disabled))
        using (var iconFont = ImRaii.PushFont(UiBuilder.IconFont))
        {
            ret = Im.Button(icon.ToIconString(), actualSize);
        }

        if (!string.IsNullOrEmpty(tooltip))
            Im.Tooltip.OnHover(HoveredFlags.AllowWhenDisabled, tooltip);

        return ret && !disabled;
    }
}
