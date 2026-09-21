using System.Numerics;
using UniversalModConverter.Session;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace UniversalModConverter.Windows.Ui;

/// <summary>
/// Semantic colours and spacing. Base colours come from the active Dalamud style so the
/// plugin follows the user's theme; only status accents are fixed.
/// </summary>
internal static class Theme
{
    public static readonly Vector4 Success = new(0.41f, 0.86f, 0.48f, 1f);
    public static readonly Vector4 Danger  = new(0.94f, 0.40f, 0.44f, 1f);
    public static readonly Vector4 Warning = new(1.00f, 0.80f, 0.40f, 1f);
    public static readonly Vector4 Info    = new(0.45f, 0.72f, 1.00f, 1f);
    public static readonly Vector4 Accent  = new(0.62f, 0.50f, 1.00f, 1f);

    /// <summary>Old values in diffs.</summary>
    public static Vector4 From => Danger;

    /// <summary>New values in diffs.</summary>
    public static Vector4 To => Success;

    public static Vector4 Muted => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
    public static Vector4 Text => ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

    public static Vector4 CardBackground
    {
        get
        {
            var frame = ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg];
            return frame with { W = frame.W * 0.45f };
        }
    }

    public static float Scale => ImGuiHelpers.GlobalScale;
    public static float Gap => 8f * Scale;
    public static float Rounding => 4f * Scale;

    public static Vector4 For(LogLevel level) => level switch
    {
        LogLevel.Success => Success,
        LogLevel.Warning => Warning,
        LogLevel.Error   => Danger,
        _                => Text,
    };

    public static Vector4 For(BannerKind kind) => kind switch
    {
        BannerKind.Success => Success,
        BannerKind.Warning => Warning,
        BannerKind.Error   => Danger,
        _                  => Info,
    };
}
