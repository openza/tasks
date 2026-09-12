using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed class SignInWindow : Window
{
    public SignInWindow(string title, Func<Func<SignInCode, Task>, CancellationToken, Task> signIn)
    {
        Title = title; Width = 520; Height = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var message = new TextBlock { Text = "Preparing sign-in…", TextWrapping = TextWrapping.Wrap };
        var codeText = new TextBox { IsReadOnly = true, FontSize = 24 };
        var open = new Button { Content = "Open sign-in page", IsEnabled = false };
        var copy = new Button { Content = "Copy code", IsEnabled = false };
        var cancel = new Button { Content = "Cancel" };
        Content = new StackPanel { Margin = new Thickness(24), Spacing = 14, Children =
        { new TextBlock { Text = title, FontSize = 22 }, message, codeText,
          new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, copy, cancel } } } };
        var cancellation = new CancellationTokenSource();
        SignInCode? currentCode = null;
        cancel.Click += (_, _) => Close(false);
        Closed += (_, _) => cancellation.Cancel();
        open.Click += async (_, _) =>
        {
            if (currentCode is not null && Uri.TryCreate(currentCode.VerificationUrl, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            {
                try { await Launcher.LaunchUriAsync(uri); }
                catch (Exception exception) { message.Text = exception.Message; }
            }
        };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is null || currentCode is null) return;
            try { await Clipboard.SetValueAsync(DataFormat.Text, currentCode.Code); }
            catch (Exception exception) { message.Text = exception.Message; }
        };
        Opened += async (_, _) =>
        {
            try
            {
                await signIn(async code => await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    currentCode = code; codeText.Text = code.Code;
                    message.Text = "Open the sign-in page and enter this code. This window will close when sign-in completes.";
                    open.IsEnabled = true; copy.IsEnabled = true;
                }), cancellation.Token);
                if (!cancellation.IsCancellationRequested) Close(true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception) { message.Text = exception.Message; cancel.Content = "Close"; }
            // Closed can occur after a failed attempt; its handler still owns cancellation.
        };
    }
}
