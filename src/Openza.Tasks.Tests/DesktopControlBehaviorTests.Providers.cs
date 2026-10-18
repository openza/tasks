using System.Net;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Desktop.Connected;
using Openza.Tasks.Desktop.Dialogs;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

public sealed partial class DesktopControlBehaviorTests
{
    [Fact]
    public Task Connected_intake_search_filters_skip_unskip_and_adopt_use_actual_controls() => Run(async context =>
    {
        for (var i = 0; i < 3; i++)
            await context.Store.UpsertProviderSourceItemAsync(new ProviderSourceItem
            {
                Id = $"source-{i}", IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default",
                ExternalId = $"external-{i}", ProviderTaskId = $"external-{i}", Title = $"Provider task {i}",
                SourceProjectId = $"project-{i}", SourceProjectName = $"Provider list {i}", SuggestedSpaceId = SpaceIds.Default, SnapshotJson = "{}",
            });
        context.Window.FindControl<Button>("ConnectedTasksCommand")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Window.FindControl<Border>("ConnectedPane")!.IsVisible && context.Vm.WaitingConnectedTaskCount == 3);
        context.Window.UpdateLayout();
        var pane = context.Window.FindControl<Border>("ConnectedPane")!;
        var search = context.Window.FindControl<TextBox>("ConnectedSearchBox")!;
        search.Text = "task 1"; await Until(() => context.Vm.FilteredConnectedTasks.Count == 1);
        search.Text = ""; await Until(() => context.Vm.FilteredConnectedTasks.Count == 3);
        foreach (var filter in pane.GetVisualDescendants().OfType<ComboBox>())
        {
            for (var index = 0; index < filter.ItemCount; index++)
            {
                filter.SelectedIndex = index;
                await Task.Delay(20);
                Assert.NotEmpty(context.Vm.FilteredConnectedTasks);
            }
            filter.SelectedIndex = 0;
        }
        context.Window.UpdateLayout();
        pane.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Skip")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => context.Vm.SkippedConnectedTaskCount == 1 && !context.Vm.IsBusy);
        pane.GetVisualDescendants().OfType<ToggleSwitch>().Single().IsChecked = true;
        await Until(() => context.Vm.FilteredConnectedTasks.Count == 3); context.Window.UpdateLayout();
        Click(pane, "Unskip"); await Until(() => context.Vm.SkippedConnectedTaskCount == 0 && !context.Vm.IsBusy);
        Click(pane, "Add all to Inbox"); await Until(() => context.Vm.WaitingConnectedTaskCount == 0 && !context.Vm.IsBusy);
        pane.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Close connected app tasks")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(pane.IsVisible);

        await context.Store.UpsertProviderSourceItemAsync(new ProviderSourceItem
        {
            Id = "legacy-source", IntegrationId = IntegrationIds.Todoist, ProviderConnectionId = "todoist_default",
            ExternalId = "legacy-external", ProviderTaskId = "legacy-external", Title = "Legacy connected task", SnapshotJson = "{}",
        });
        var dialog = new ConnectedTasksWindow(context.Vm); dialog.Show(context.Window);
        await Until(() => context.Vm.WaitingConnectedTaskCount == 1 && !context.Vm.IsBusy); dialog.UpdateLayout();
        Click(dialog, "Refresh"); await Until(() => !context.Vm.IsBusy);
        dialog.FindControl<TextBox>("SearchBox")!.Text = "Legacy";
        await Until(() => context.Vm.FilteredConnectedTasks.Count == 1);
        dialog.UpdateLayout(); Click(dialog, "Skip"); await Until(() => context.Vm.SkippedConnectedTaskCount == 1 && !context.Vm.IsBusy);
        dialog.UpdateLayout(); Click(dialog, "Unskip"); await Until(() => context.Vm.SkippedConnectedTaskCount == 0 && !context.Vm.IsBusy);
        dialog.UpdateLayout();
        var primary = context.Vm.FilteredConnectedTasks.Single().PrimaryActionText;
        Click(dialog, primary); await Until(() => context.Vm.WaitingConnectedTaskCount == 0 && !context.Vm.IsBusy);
        Click(dialog, "Close"); Assert.False(dialog.IsVisible);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task GitHub_issue_form_validates_filters_loads_labels_and_handles_submit(bool fail) => Run(async context =>
    {
        var handler = new IssueFormHandler { Fail = fail };
        using var http = new HttpClient(handler);
        var credentials = new InMemoryCredentialStore(); await credentials.SaveAsync(GitHubIssueService.TokenKey, "synthetic-test-token");
        var vm = new MainWindowViewModel(context.Store, credentials, preferencesStore: context.Preferences, gitHubIssueService: new GitHubIssueService(http));
        var draft = new GitHubIssueDraft("task-0", "", "Body", "", [new("example", "repo", "example/repo", false, true, false)], null);
        var dialog = new GitHubIssueWindow(vm, draft);
        var result = dialog.ShowDialog<string?>(context.Window);
        dialog.UpdateLayout();
        Click(dialog, "Create issue"); Assert.False(result.IsCompleted);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Choose a repository and enter a title.");
        T Named<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
        Named<TextBox>("Find repository").Text = "missing";
        await Until(() => Named<ComboBox>("Repository").ItemCount == 0);
        Named<TextBox>("Find repository").Text = "repo";
        await Until(() => Named<ComboBox>("Repository").ItemCount == 1);
        Named<ComboBox>("Repository").SelectedIndex = 0;
        await Until(() => Named<ComboBox>("Issue label").ItemCount == 2);
        Named<ComboBox>("Issue label").SelectedIndex = 1;
        Named<TextBox>("Issue title").Text = "  Synthetic issue 日本語  ";
        Named<TextBox>("Issue body").Text = "Line one\nLine two";
        Click(dialog, "Create issue");
        await Until(() => handler.Submissions == 1);
        if (fail)
        {
            await Until(() => FindButton(dialog, "Create issue").IsEnabled);
            Assert.False(result.IsCompleted);
            Assert.Empty(await context.Store.GetTaskExternalLinksAsync("task-0"));
            SaveFrame(dialog, "github-issue-error");
            Click(dialog, "Cancel"); Assert.Null(await result);
        }
        else
        {
            Assert.Equal("https://github.com/example/repo/issues/2", await result);
            Assert.Single(await context.Store.GetTaskExternalLinksAsync("task-0"));
            Assert.Contains("Synthetic issue", handler.Body); Assert.Contains("bug", handler.Body);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Personal_cloud_management_confirmation_guards_and_actions_use_simulated_service(bool remove) => Run(async context =>
    {
        var service = new ControlCloudService();
        var vm = new MainWindowViewModel(context.Store, new InMemoryCredentialStore(), preferencesStore: context.Preferences, personalCloudSetup: service);
        var dialog = new PersonalCloudManageWindow(vm);
        var result = dialog.ShowDialog<bool>(context.Window); dialog.UpdateLayout();
        Click(dialog, "Open tasks"); Assert.Equal(1, service.OpenCalls);
        Click(dialog, "Update"); await Until(() => service.UpdateCalls == 1 && !vm.IsConnectingPersonalCloud);
        Click(dialog, "Disconnect"); Assert.True(dialog.FindControl<StackPanel>("DisconnectConfirmation")!.IsVisible);
        Click(dialog, "Cancel"); Assert.False(dialog.FindControl<StackPanel>("DisconnectConfirmation")!.IsVisible);
        var confirmation = dialog.FindControl<TextBox>("RemoveConfirmationText")!;
        confirmation.Text = "remove"; await Task.Delay(20); Assert.False(dialog.FindControl<Button>("RemoveButton")!.IsEnabled);
        confirmation.Text = "REMOVE "; await Task.Delay(20); Assert.False(dialog.FindControl<Button>("RemoveButton")!.IsEnabled);
        confirmation.Text = "REMOVE"; await Until(() => dialog.FindControl<Button>("RemoveButton")!.IsEnabled);
        if (remove) Click(dialog, "Remove permanently");
        else { Click(dialog, "Disconnect"); Click(dialog, "Yes, disconnect"); }
        Assert.True(await result);
        Assert.Equal(remove ? 1 : 0, service.RemoveCalls);
        Assert.Equal(remove ? 0 : 1, service.DisconnectCalls);
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
    });

    [Fact]
    public Task Personal_cloud_settings_launcher_opens_management_and_close_returns_to_settings() => Run(async context =>
    {
        var vm = new MainWindowViewModel(context.Store, new InMemoryCredentialStore(), preferencesStore: context.Preferences, personalCloudSetup: new ControlCloudService());
        var window = new Openza.Tasks.Desktop.Shell.MainWindow(vm) { Width = 1440, Height = 900 }; window.Show(context.Window);
        try
        {
            await Until(() => vm.StatusMessage.StartsWith("Local data") && !vm.IsBusy);
            window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var settings = window.FindControl<ScrollViewer>("SettingsWorkspace")!;
            await Until(() => settings.IsVisible); window.UpdateLayout();
            Click(settings, vm.PersonalCloudActionText);
            await Until(() => window.OwnedWindows.OfType<PersonalCloudManageWindow>().Any());
            Click(window.OwnedWindows.OfType<PersonalCloudManageWindow>().Single(), "Close");
            Assert.True(settings.IsVisible); Assert.True(vm.IsPersonalCloudConnected);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Personal_cloud_pairing_form_back_validation_and_cancel_preserve_local_data() => Run(async context =>
    {
        var dialog = new PersonalCloudSetupWindow(context.Vm);
        dialog.Show(context.Window); dialog.UpdateLayout();
        Click(dialog, "Pair existing cloud"); Assert.True(dialog.FindControl<StackPanel>("PairPanel")!.IsVisible);
        dialog.FindControl<TextBox>("ExistingCloudAddress")!.Text = "invalid address";
        dialog.FindControl<TextBox>("ExistingPairingGrant")!.Text = "synthetic code";
        dialog.FindControl<CheckBox>("ExistingProviderBridge")!.IsChecked = false;
        dialog.FindControl<Button>("PairExistingButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => dialog.FindControl<TextBlock>("PairingError")!.IsVisible);
        Assert.True(dialog.FindControl<Button>("PairExistingButton")!.IsEnabled);
        Click(dialog, "Back"); Assert.False(dialog.FindControl<StackPanel>("PairPanel")!.IsVisible);
        Click(dialog, "Cancel"); Assert.False(dialog.IsVisible);
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Personal_cloud_setup_progress_failure_and_cancel_leave_the_dialog_recoverable(bool cancel) => Run(async context =>
    {
        var service = new ControlCloudService(disconnected: true) { WaitForSetup = cancel };
        var vm = new MainWindowViewModel(context.Store, new InMemoryCredentialStore(), preferencesStore: context.Preferences, personalCloudSetup: service);
        var dialog = new PersonalCloudSetupWindow(vm); dialog.Show(context.Window); dialog.UpdateLayout();
        Click(dialog, "Continue to Cloudflare");
        await Until(() => dialog.FindControl<StackPanel>("ProgressPanel")!.IsVisible);
        if (cancel)
        {
            await Until(() => service.SetupCancellation.CanBeCanceled);
            Click(dialog, "Cancel setup");
            await Until(() => !dialog.IsVisible);
            Assert.True(service.SetupCancellation.IsCancellationRequested);
            Assert.Contains("cancelled", vm.PersonalCloudSummaryText);
        }
        else
        {
            await Until(() => dialog.FindControl<TextBlock>("ProgressTitle")!.Text == "Setup needs attention");
            Assert.False(dialog.FindControl<ProgressBar>("SetupProgressBar")!.IsVisible);
            Click(dialog, "Close and try again"); Assert.False(dialog.IsVisible);
        }
        Assert.NotNull(await context.Store.GetTaskAsync("task-0"));
    });

    private sealed class IssueFormHandler : HttpMessageHandler
    {
        public bool Fail { get; init; }
        public int Submissions { get; private set; }
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new StringContent("[{\"name\":\"bug\",\"color\":\"123456\"}]") };
            Submissions++; Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(Fail ? HttpStatusCode.Forbidden : HttpStatusCode.Created)
            {
                Content = new StringContent(Fail ? "{\"message\":\"Synthetic permission failure\"}" :
                    "{\"id\":2,\"number\":2,\"title\":\"Synthetic issue\",\"html_url\":\"https://github.com/example/repo/issues/2\",\"state\":\"open\"}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ControlCloudService : IPersonalCloudSetupService
    {
        public ControlCloudService(bool disconnected = false) { if (disconnected) CurrentConnection = null; }
        public bool WaitForSetup { get; init; }
        public CancellationToken SetupCancellation { get; private set; }
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile? CurrentConnection { get; private set; } = new()
        {
            ServiceOrigin = new Uri("https://cloud.example/"), InstanceId = "test", DeviceId = "test", ArtifactVersion = "1.0.0", ConnectedAt = DateTimeOffset.UtcNow,
        };
        public int OpenCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public Task OpenAsync(CancellationToken cancellationToken = default) { OpenCalls++; return Task.CompletedTask; }
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) { UpdateCalls++; return Task.FromResult(new PersonalCloudUpgradeResult("1.0.1")); }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { DisconnectCalls++; CurrentConnection = null; return Task.CompletedTask; }
        public Task RemoveAsync(CancellationToken cancellationToken = default) { RemoveCalls++; CurrentConnection = null; return Task.CompletedTask; }
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PersonalCloudSyncResult(0, 0, 0));
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<PersonalCloudProvisioningResult> ConnectAsync(CancellationToken cancellationToken = default, IProgress<PersonalCloudSetupProgress>? progress = null)
        {
            SetupCancellation = cancellationToken;
            progress?.Report(new(PersonalCloudSetupStep.AwaitingAuthorization));
            if (WaitForSetup) await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new IOException("Synthetic setup failure");
        }
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(Uri serviceOrigin, string grant, string deviceName, bool providerBridge, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
