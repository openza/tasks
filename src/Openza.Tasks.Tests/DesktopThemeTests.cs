using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Tests;

[Collection(nameof(EditorTestCollection))]
public sealed class DesktopThemeTests(EditorTestSession fixture)
{
    private static HighContrastColors Black => new(Colors.Black, Colors.White, Colors.Yellow,
        Colors.Black, Colors.Black, Colors.White, Colors.Lime);
    private static HighContrastColors White => new(Colors.White, Colors.Black, Colors.Navy,
        Colors.White, Colors.White, Colors.Black, Colors.Gray);

    [Theory]
    [InlineData(PlatformThemeVariant.Dark)]
    [InlineData(PlatformThemeVariant.Light)]
    public void Fallback_selection_palette_is_distinct_from_normal_text(PlatformThemeVariant theme)
    {
        var colors = HighContrastColors.ReadFallback(new() { ThemeVariant = theme });
        Assert.NotEqual(colors.Foreground, colors.Highlight);
        Assert.NotEqual(colors.Highlight, colors.HighlightText);
        Assert.NotEqual(colors.Background, colors.Foreground);
    }

    [Theory]
    [InlineData(PlatformThemeVariant.Dark)]
    [InlineData(PlatformThemeVariant.Light)]
    public async Task Prompt_selection_uses_highlight_text_and_restores_normal_foreground(PlatformThemeVariant theme)
    {
        await fixture.Session.Dispatch(() =>
        {
            var application = Avalonia.Application.Current!;
            var original = application.RequestedThemeVariant;
            using var controller = new DesktopThemeController(application, HighContrastColors.ReadFallback);
            var dialog = new TextPromptWindow("Selection", "Selected value");
            try
            {
                controller.SetPreference("Light");
                controller.UpdateColors(new());
                dialog.Show();
                Dispatcher.UIThread.RunJobs();
                var value = dialog.FindControl<TextBox>("ValueBox")!;
                var normalForeground = value.SelectionForegroundBrush;
                var normalBackground = value.SelectionBrush;
                Assert.Null(normalForeground);
                Assert.Equal(value.Text!.Length, Math.Abs(value.SelectionEnd - value.SelectionStart));

                var values = new PlatformColorValues { ThemeVariant = theme, ContrastPreference = ColorContrastPreference.High };
                var colors = HighContrastColors.ReadFallback(values);
                controller.UpdateColors(values);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(colors.Highlight, Assert.IsAssignableFrom<ISolidColorBrush>(value.SelectionBrush).Color);
                Assert.Equal(colors.HighlightText, Assert.IsAssignableFrom<ISolidColorBrush>(value.SelectionForegroundBrush).Color);

                controller.UpdateColors(new());
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(normalForeground, value.SelectionForegroundBrush);
                Assert.Equal(normalBackground, value.SelectionBrush);
            }
            finally
            {
                dialog.Close();
                application.RequestedThemeVariant = original;
            }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("Dark", false)]
    [InlineData("Light", true)]
    [InlineData(null, true)]
    public void High_contrast_takes_priority_over_saved_theme_and_uses_system_background(string? preference, bool dark)
    {
        var values = new PlatformColorValues { ContrastPreference = ColorContrastPreference.High };
        Assert.Equal(dark ? DesktopThemeController.HighContrastDark : DesktopThemeController.HighContrastLight,
            DesktopThemeController.SelectTheme(preference, values, dark ? Black : White));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("System")]
    [InlineData(null)]
    [InlineData("unknown")]
    public void Ordinary_theme_selection_keeps_existing_preferences(string? preference)
    {
        var expected = preference == "Light" ? ThemeVariant.Light : preference == "Dark" ? ThemeVariant.Dark : ThemeVariant.Default;
        Assert.Equal(expected, DesktopThemeController.SelectTheme(preference, new PlatformColorValues()));
    }

    [Fact]
    public void Every_custom_brush_has_an_opaque_high_contrast_override()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../../src/Openza.Tasks.Desktop/Styles/DesignTokens.axaml");
        var document = XDocument.Load(Path.GetFullPath(path));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = DesktopThemeController.CreateResources(Black);
        foreach (var key in document.Descendants().Where(node => node.Name.LocalName == "SolidColorBrush")
            .Select(node => (string)node.Attribute(x + "Key")!).Distinct())
        {
            var brush = Assert.IsType<SolidColorBrush>(resources[key]);
            Assert.Equal(255, brush.Color.A);
        }
    }

    [Fact]
    public async Task Existing_dialog_updates_system_colors_live_and_restores_preference_when_contrast_ends()
    {
        await fixture.Session.Dispatch(() =>
        {
            var application = Avalonia.Application.Current!;
            var original = application.RequestedThemeVariant;
            var palette = Black;
            using var controller = new DesktopThemeController(application, _ => palette);
            var dialog = new AddTaskWindow();
            try
            {
                controller.SetPreference("Light");
                controller.UpdateColors(new() { ContrastPreference = ColorContrastPreference.High });
                dialog.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(DesktopThemeController.HighContrastDark, dialog.ActualThemeVariant);
                Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(dialog.Background).Color);
                var title = dialog.FindControl<TextBox>("TitleBox")!;
                Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(title.Foreground).Color);
                Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(title.BorderBrush).Color);
                Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(title.PlaceholderForeground).Color);

                controller.SetPreference("Dark");
                Assert.Equal(DesktopThemeController.HighContrastDark, application.RequestedThemeVariant);
                palette = White;
                controller.UpdateColors(new() { ContrastPreference = ColorContrastPreference.High });
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(DesktopThemeController.HighContrastLight, dialog.ActualThemeVariant);
                Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(dialog.Background).Color);
                Assert.Equal(Colors.Black, Assert.IsAssignableFrom<ISolidColorBrush>(title.Foreground).Color);
                controller.UpdateColors(new());
                Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
                controller.Dispose();
                controller.UpdateColors(new() { ContrastPreference = ColorContrastPreference.High });
                Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);
            }
            finally
            {
                dialog.Close();
                application.RequestedThemeVariant = original;
            }
            return true;
        }, CancellationToken.None);
    }
}
