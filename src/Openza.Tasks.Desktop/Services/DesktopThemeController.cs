using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Openza.Tasks.Desktop.Services;

public sealed record HighContrastColors(Color Background, Color Foreground, Color Highlight,
    Color HighlightText, Color ButtonFace, Color ButtonText, Color DisabledText)
{
    public static HighContrastColors Read(PlatformColorValues values)
    {
        if (OperatingSystem.IsWindows())
            return new(ReadSystemColor(5), ReadSystemColor(8), ReadSystemColor(13),
                ReadSystemColor(14), ReadSystemColor(15), ReadSystemColor(18), ReadSystemColor(17));
        return ReadFallback(values);
    }

    public static HighContrastColors ReadFallback(PlatformColorValues values)
    {
        var dark = values.ThemeVariant == PlatformThemeVariant.Dark;
        var background = dark ? Colors.Black : Colors.White;
        var foreground = dark ? Colors.White : Colors.Black;
        var highlight = dark ? Colors.Yellow : Colors.Navy;
        return new(background, foreground, highlight, background, background, foreground, foreground);
    }

    private static Color ReadSystemColor(int index)
    {
        var color = GetSysColor(index);
        return Color.FromRgb((byte)color, (byte)(color >> 8), (byte)(color >> 16));
    }

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}

public sealed class DesktopThemeController : IDisposable
{
    public static ThemeVariant HighContrastLight { get; } = new("HighContrastLight", ThemeVariant.Light);
    public static ThemeVariant HighContrastDark { get; } = new("HighContrastDark", ThemeVariant.Dark);
    private readonly Avalonia.Application _application;
    private readonly IPlatformSettings? _settings;
    private readonly Func<PlatformColorValues, HighContrastColors> _readColors;
    private PlatformColorValues _values;
    private string? _preference;
    private bool _disposed;

    public DesktopThemeController(Avalonia.Application application, Func<PlatformColorValues, HighContrastColors>? readColors = null)
    {
        _application = application;
        _readColors = readColors ?? HighContrastColors.Read;
        _settings = application.PlatformSettings;
        _values = _settings?.GetColorValues() ?? new PlatformColorValues();
        if (_settings is not null) _settings.ColorValuesChanged += OnColorValuesChanged;
        Apply();
    }

    public void SetPreference(string? preference)
    {
        _preference = preference;
        Apply();
    }

    public void UpdateColors(PlatformColorValues values)
    {
        if (_disposed) return;
        _values = values;
        Apply();
    }

    public static ThemeVariant SelectTheme(string? preference, PlatformColorValues values, HighContrastColors? colors = null)
    {
        if (values.ContrastPreference == ColorContrastPreference.High)
        {
            var dark = colors is null ? values.ThemeVariant == PlatformThemeVariant.Dark
                : colors.Background.R + colors.Background.G + colors.Background.B < 384;
            return dark ? HighContrastDark : HighContrastLight;
        }
        return preference switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues values) =>
        Dispatcher.UIThread.Post(() => UpdateColors(values));

    private void Apply()
    {
        var colors = _values.ContrastPreference == ColorContrastPreference.High ? _readColors(_values) : null;
        var theme = SelectTheme(_preference, _values, colors);
        if (colors is not null) _application.Resources.ThemeDictionaries[theme] = CreateResources(colors);
        _application.RequestedThemeVariant = theme;
    }

    public static ResourceDictionary CreateResources(HighContrastColors colors)
    {
        var resources = new ResourceDictionary();
        void Brushes(Color color, params string[] keys)
        {
            foreach (var key in keys) resources[key] = new SolidColorBrush(color);
        }
        Brushes(colors.Background, "OpenzaAppCanvasBrush", "OpenzaPaneBackgroundBrush", "OpenzaDetailPaneBackgroundBrush",
            "OpenzaElevatedSurfaceBrush", "OpenzaSurfaceAltBrush", "OpenzaRowBackgroundBrush", "OpenzaRowHoverBrush",
            "OpenzaRowSelectedBrush", "OpenzaChipBackgroundBrush", "OpenzaNavSelectedBrush", "OpenzaNavSelectedHoverBrush",
            "OpenzaNavSelectedPressedBrush");
        Brushes(colors.Foreground, "OpenzaTextPrimaryBrush", "OpenzaTextSecondaryBrush", "OpenzaTextTertiaryBrush",
            "OpenzaBorderBrush", "OpenzaSeparatorBrush", "OpenzaChipForegroundBrush", "OpenzaDangerBrush",
            "OpenzaSelectionBorderBrush");
        Brushes(colors.Background, "OpenzaOnDangerBrush");
        Brushes(colors.Highlight, "OpenzaAccentBrush", "OpenzaAccentHoverBrush", "OpenzaAccentPressedBrush");
        Brushes(colors.HighlightText, "OpenzaOnAccentBrush");
        resources["OpenzaSelectionBorderThickness"] = new Thickness(2);

        string[] states = ["", "PointerOver", "Pressed", "Focused", "Unfocused", "FocusedPressed", "Disabled"];
        foreach (var state in states)
        {
            foreach (var control in new[] { "Button", "ToggleButton", "TextControl", "TextControlButton", "ComboBox",
                "ComboBoxItem", "ListBoxItem", "ListViewItem", "MenuFlyoutItem", "CalendarDatePicker", "DatePickerButton", "TimePickerButton" })
            {
                Brushes(colors.Background, control + "Background" + state);
                Brushes(state == "Disabled" ? colors.DisabledText : colors.Foreground, control + "Foreground" + state);
                Brushes(state == "Focused" || state == "PointerOver" ? colors.Highlight : colors.Foreground,
                    control + "BorderBrush" + state);
            }
            Brushes(colors.Foreground, "TextControlPlaceholderForeground" + state, "ComboBoxPlaceHolderForeground" + state,
                "ComboBoxDropDownGlyphForeground" + state, "CalendarDatePickerTextForeground" + state);
            foreach (var control in new[] { "ComboBoxItem", "ListBoxItem", "ListViewItem", "ToggleButton" })
            {
                foreach (var selection in new[] { "Selected", "Checked" })
                {
                    Brushes(colors.Highlight, control + "Background" + selection + state);
                    Brushes(colors.HighlightText, control + "Foreground" + selection + state);
                    Brushes(colors.Foreground, control + "BorderBrush" + selection + state);
                }
            }
            foreach (var check in new[] { "Unchecked", "Checked", "Indeterminate" })
            {
                Brushes(colors.Background, "CheckBoxBackground" + check + state);
                Brushes(colors.Foreground, "CheckBoxForeground" + check + state, "CheckBoxBorderBrush" + check + state);
                Brushes(check == "Unchecked" ? colors.Background : colors.Highlight, "CheckBoxCheckBackgroundFill" + check + state);
                Brushes(colors.Foreground, "CheckBoxCheckBackgroundStroke" + check + state);
                Brushes(colors.HighlightText, "CheckBoxCheckGlyphForeground" + check + state);
            }
            Brushes(colors.Background, "RadioButtonBackground" + state, "RadioButtonOuterEllipseFill" + state,
                "ToggleSwitchFillOff" + state, "ToggleSwitchContainerBackground" + state);
            Brushes(colors.Foreground, "RadioButtonForeground" + state, "RadioButtonBorderBrush" + state,
                "RadioButtonOuterEllipseStroke" + state, "RadioButtonOuterEllipseCheckedStroke" + state,
                "ToggleSwitchStrokeOff" + state, "ToggleSwitchStrokeOn" + state, "ToggleSwitchKnobFillOff" + state);
            Brushes(colors.Highlight, "RadioButtonOuterEllipseCheckedFill" + state, "ToggleSwitchFillOn" + state);
            Brushes(colors.HighlightText, "RadioButtonCheckGlyphFill" + state, "RadioButtonCheckGlyphStroke" + state,
                "ToggleSwitchKnobFillOn" + state);
        }
        Brushes(colors.Background, "ComboBoxDropDownBackground", "FlyoutPresenterBackground", "MenuFlyoutPresenterBackground",
            "ToolTipBackground", "SystemControlBackgroundAltHighBrush", "SystemControlPageBackgroundChromeLowBrush");
        Brushes(colors.Foreground, "ComboBoxDropDownBorderBrush", "FlyoutBorderThemeBrush", "MenuFlyoutPresenterBorderBrush",
            "ToolTipBorderBrush", "ToolTipForeground", "ToggleSwitchContentForeground", "MenuFlyoutItemKeyboardAcceleratorTextForeground");
        Brushes(colors.Highlight, "TextControlSelectionHighlightColor");
        Brushes(colors.HighlightText, "OpenzaTextSelectionForegroundBrush");
        Brushes(colors.Foreground, "SystemControlForegroundBaseHighBrush", "SystemControlForegroundBaseMediumBrush",
            "SystemControlForegroundBaseMediumHighBrush", "SystemControlHighlightAltBaseHighBrush",
            "SystemControlHighlightBaseHighBrush", "SystemControlFocusVisualPrimaryBrush");
        Brushes(colors.Background, "SystemControlHighlightListLowBrush", "SystemControlHighlightListMediumBrush",
            "SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush",
            "SystemControlHighlightListAccentHighBrush", "SystemControlFocusVisualSecondaryBrush");
        Brushes(colors.DisabledText, "SystemControlDisabledBaseMediumLowBrush", "SystemControlDisabledBaseLowBrush");
        resources["SystemAccentColor"] = colors.Highlight;
        return resources;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_settings is not null) _settings.ColorValuesChanged -= OnColorValuesChanged;
    }
}
