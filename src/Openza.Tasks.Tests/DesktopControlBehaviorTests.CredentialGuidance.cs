using Avalonia.Controls;
using Avalonia.VisualTree;
using Openza.Tasks.Desktop.Dialogs;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Fact]
    public Task Credential_guidance_is_selectable_and_scrollable() => Run(async context =>
    {
        const string message = "Could not save the credential: Your desktop does not provide the Secret portal, so this Snap needs keyring access. Run:\n\nsudo snap connect openza-tasks:password-manager-service\n\nThen try again.";
        context.Vm.ReportError(message);
        context.Window.UpdateLayout();
        var overlay = context.Window.FindControl<Border>("StatusOverlay")!;
        var status = overlay.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
        Assert.Equal(message, status.Text);
        status.SelectionStart = 0;
        status.SelectionEnd = message.Length;
        Assert.Equal(message, status.SelectedText);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, status.TextWrapping);
        Assert.Equal(240, overlay.GetVisualDescendants().OfType<ScrollViewer>().First().MaxHeight);

        var signIn = new SignInWindow("Test sign-in", (_, _) => Task.FromException(new InvalidOperationException(message)));
        signIn.Show(context.Window);
        try
        {
            await Until(() => signIn.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == message));
            var error = signIn.GetVisualDescendants().OfType<SelectableTextBlock>().Single(text => text.Text == message);
            error.SelectionStart = 0;
            error.SelectionEnd = message.Length;
            Assert.Equal(message, error.SelectedText);
            Assert.NotNull(error.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault());
        }
        finally { signIn.Close(); }
    });
}
