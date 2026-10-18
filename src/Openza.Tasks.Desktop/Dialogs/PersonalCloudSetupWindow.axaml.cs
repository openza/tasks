using Avalonia.Controls;
using Avalonia.Interactivity;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Desktop.Dialogs;

public sealed partial class PersonalCloudSetupWindow : Window
{
    private static readonly PersonalCloudSetupStep[] OrderedSteps =
    [
        PersonalCloudSetupStep.AwaitingAuthorization,
        PersonalCloudSetupStep.ResolvingAccount,
        PersonalCloudSetupStep.CreatingDatabase,
        PersonalCloudSetupStep.DeployingApplication,
        PersonalCloudSetupStep.PreparingDatabase,
        PersonalCloudSetupStep.VerifyingConnection,
        PersonalCloudSetupStep.PairingDesktop,
        PersonalCloudSetupStep.CreatingOwnerAccess,
    ];

    private readonly MainWindowViewModel _viewModel;
    private bool _setupStarted;
    private bool _setupFinished;
    private CancellationTokenSource? _setupCancellation;

    public PersonalCloudSetupWindow()
    {
        InitializeComponent();
        _viewModel = null!;
    }

    public PersonalCloudSetupWindow(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Title = "Set up Personal Cloud";
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnPairExistingClicked(object? sender, RoutedEventArgs e)
    {
        ReviewPanel.IsVisible = false;
        PairPanel.IsVisible = true;
        Title = "Pair existing Personal Cloud";
    }

    private void OnPairBackClicked(object? sender, RoutedEventArgs e)
    {
        PairPanel.IsVisible = false;
        ReviewPanel.IsVisible = true;
        PairingError.IsVisible = false;
        Title = "Set up Personal Cloud";
    }

    private async void OnConfirmPairExistingClicked(object? sender, RoutedEventArgs e)
    {
        if (_setupStarted) return;
        _setupStarted = true;
        _setupCancellation = new CancellationTokenSource();
        PairExistingButton.IsEnabled = false;
        PairingError.IsVisible = false;
        var result = await _viewModel.PairExistingPersonalCloudAsync(
            ExistingCloudAddress.Text ?? string.Empty,
            ExistingPairingGrant.Text ?? string.Empty,
            ExistingProviderBridge.IsChecked == true,
            _setupCancellation.Token);
        if (result is not null)
        {
            _setupFinished = true;
            Close();
            return;
        }
        _setupStarted = false;
        _setupCancellation.Dispose();
        _setupCancellation = null;
        PairExistingButton.IsEnabled = true;
        PairingError.Text = _viewModel.PersonalCloudSummaryText;
        PairingError.IsVisible = true;
    }

    private async void OnContinueClicked(object? sender, RoutedEventArgs e)
    {
        if (_setupStarted) return;
        _setupStarted = true;
        _setupCancellation = new CancellationTokenSource();
        ReviewPanel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        CloseButton.IsVisible = true;

        var progress = new Progress<PersonalCloudSetupProgress>(UpdateProgress);
        try
        {
            var result = await _viewModel.ConnectPersonalCloudAsync(_setupCancellation.Token, progress);
            if (_setupCancellation.IsCancellationRequested)
            {
                _setupFinished = true;
                Close();
                return;
            }
            if (result is null)
            {
                if (_viewModel.IsPersonalCloudConnected)
                {
                    _setupFinished = true;
                    ProgressTitle.Text = "Your cloud is ready";
                    ProgressSummary.Text = _viewModel.PersonalCloudSummaryText;
                    CloseButton.Content = "Close";
                    return;
                }
                ShowFailure(_viewModel.PersonalCloudSummaryText);
                return;
            }

            UpdateProgress(new PersonalCloudSetupProgress(PersonalCloudSetupStep.Complete));
            ProgressTitle.Text = "Personal Cloud is ready";
            ProgressSummary.Text = _viewModel.InitialSyncProgress is { IsComplete: true }
                ? "Your cloud is ready. Initial sync is complete. Finish passkey security in the browser window Openza opened."
                : "Your cloud is ready. Upload progress remains available in Tasks and Settings. Finish passkey security in the browser window Openza opened.";
            AddressText.Text = result.ServiceOrigin.AbsoluteUri;
            AddressPanel.IsVisible = true;
            _setupFinished = true;
            CloseButton.IsVisible = true;
            CloseButton.Content = "Close";
        }
        catch
        {
            ShowFailure("Personal Cloud setup stopped safely. Close this window and choose Set up Personal Cloud to resume.");
        }
    }

    private void UpdateProgress(PersonalCloudSetupProgress progress)
    {
        if (progress.Step == PersonalCloudSetupStep.Complete)
        {
            SetupProgressBar.Value = 9;
            SetStepStates(OrderedSteps.Length);
            return;
        }

        var index = Array.IndexOf(OrderedSteps, progress.Step);
        if (index < 0) return;
        SetupProgressBar.Value = index + 1;
        SetStepStates(index);
        ProgressSummary.Text = progress.Step == PersonalCloudSetupStep.AwaitingAuthorization
            ? "Approve the requested permissions in the Cloudflare page opened in your browser."
            : "Keep Openza open while it completes this step. Completed work is saved safely.";
    }

    private void SetStepStates(int currentIndex)
    {
        var controls = new[] { AuthorizationStep, AccountStep, DatabaseStep, ApplicationStep, MigrationStep, HealthStep, PairingStep, OwnerAccessStep };
        var labels = new[] { "Authorize Cloudflare", "Confirm Cloudflare account", "Create database", "Deploy Web app", "Prepare database", "Verify connection", "Pair this desktop", "Finish security setup" };
        for (var index = 0; index < controls.Length; index++)
        {
            var marker = index < currentIndex ? "✓" : index == currentIndex ? "●" : "○";
            controls[index].Text = $"{marker} {labels[index]}";
        }
    }

    private void ShowFailure(string message)
    {
        _setupFinished = true;
        ProgressTitle.Text = "Setup needs attention";
        ProgressSummary.Text = message;
        SetupProgressBar.IsVisible = false;
        CloseButton.IsVisible = true;
        CloseButton.Content = "Close and try again";
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (_setupStarted && !_setupFinished)
        {
            CloseButton.IsEnabled = false;
            CloseButton.Content = "Cancelling…";
            ProgressSummary.Text = "Stopping setup safely…";
            _setupCancellation?.Cancel();
            return;
        }
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_setupStarted || _setupFinished) return;
        e.Cancel = true;
        OnCloseClicked(sender, new RoutedEventArgs());
    }
}
