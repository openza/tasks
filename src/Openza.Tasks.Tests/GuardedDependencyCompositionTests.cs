using System.Reflection;
using System.Reflection.Emit;
using Openza.Tasks.Application.Sync;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Sync;
using Openza.Tasks.Desktop.Services;
using Openza.Tasks.Desktop.ViewModels;

namespace Openza.Tasks.Tests;

// Synthetic dependency-boundary coverage only. The real Windows session remains on its separate branch.
public sealed class GuardedDependencyCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-guard-composition", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(typeof(DesktopSyncProviderResolver))]
    [InlineData(typeof(DesktopMicrosoftAuthService))]
    [InlineData(typeof(ConnectedSyncApplicationService))]
    public void Compiled_constructor_uses_resolved_fields_even_when_raw_and_resolved_credentials_later_diverge(Type dependency)
    {
        var constructor = typeof(MainWindowViewModel).GetConstructors().Single(x => x.GetParameters().Length > 1);
        var instructions = ReadInstructions(constructor);
        var index = instructions.FindIndex(x => x.Code == OpCodes.Newobj && x.Member?.DeclaringType == dependency);
        Assert.True(index > 0, $"Missing {dependency.Name} construction in the view-model constructor.");
        string[] expected = dependency == typeof(DesktopSyncProviderResolver)
            ? ["field:_credentials", "field:_preferencesStore", "field:_microsoftAuth", "field:_todoistSyncProviderFactory", "field:_microsoftSyncProviderFactory"]
            : [dependency == typeof(DesktopMicrosoftAuthService) ? "field:_credentials" : "field:_personalCloudSetup"];
        var arguments = new string[expected.Length];
        var cursor = index - 1;
        for (var argument = arguments.Length - 1; argument >= 0; argument--)
        {
            while (instructions[cursor].Code == OpCodes.Nop) cursor--;
            var value = instructions[cursor--];
            if (value.Code == OpCodes.Ldfld)
            {
                Assert.Equal(typeof(MainWindowViewModel), value.Member?.DeclaringType);
                Assert.Equal(0, instructions[cursor--].ArgumentIndex); // Field receiver is this, not a captured closure.
                arguments[argument] = "field:" + value.Member!.Name;
            }
            else
            {
                Assert.True(value.ArgumentIndex > 0, $"Unexpected {value.Code.Name} dependency expression for {dependency.Name}.");
                // Instance constructor arg0 is this; arg1 is parameter0 (store), arg2 is credentials.
                arguments[argument] = "argument:" + constructor.GetParameters()[value.ArgumentIndex!.Value - 1].Name;
            }
        }
        Assert.Equal(expected, arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composed_services_retain_the_view_models_resolved_dependency_identities(bool defaultMicrosoftAuth)
    {
        var fixture = await CreateAsync(inhibited: true, defaultMicrosoftAuth);
        var service = Field<ConnectedSyncApplicationService>(fixture.Vm);
        var resolver = Assert.IsType<DesktopSyncProviderResolver>(Field<IConnectedSyncProviderResolver>(service));
        Assert.Same(Field<ICredentialStore>(fixture.Vm), Field<ICredentialStore>(resolver));
        Assert.Same(Field<IDesktopMicrosoftAuthService>(fixture.Vm), Field<IDesktopMicrosoftAuthService>(resolver));
        Assert.Same(Field<IPersonalCloudSetupService>(fixture.Vm), Field<IPersonalCloudSetupService>(service));
        if (defaultMicrosoftAuth)
            Assert.Same(Field<ICredentialStore>(fixture.Vm), Field<ICredentialStore>(Field<IDesktopMicrosoftAuthService>(fixture.Vm)));
    }

    [Fact]
    public async Task Inhibited_dependencies_block_underlying_vault_and_network_but_preserve_local_work_and_pairing()
    {
        var fixture = await CreateAsync(inhibited: true);
        var preferences = await File.ReadAllBytesAsync(fixture.SettingsPath);
        var pairing = await File.ReadAllBytesAsync(fixture.PairingPath);
        await fixture.Vm.InitializeAsync();
        await fixture.Vm.SelectTaskAsync(Assert.Single(fixture.Vm.Tasks));
        Assert.True(fixture.Vm.AutomaticSyncEnabled); // Automatic sync must actually exercise its guarded path.
        var before = (await fixture.Store.GetTaskAsync("local"))!;
        var pending = Assert.Single(await fixture.Store.GetPendingPersonalCloudOutboxAsync("synthetic-instance", "synthetic-device"));

        await fixture.Vm.RunTodoistSyncAsync();
        await fixture.Vm.RunAutomaticTodoistSyncAsync();
        Assert.Equal(2, fixture.Backend.CloudGuardAttempts);
        Assert.Equal(2, fixture.Backend.AuthGuardAttempts);
        Assert.Contains("Synthetic session inhibited", fixture.Vm.LastSyncResult);
        Assert.Equivalent(before, await fixture.Store.GetTaskAsync("local"), strict: true);
        Assert.Equal(pending, Assert.Single(await fixture.Store.GetPendingPersonalCloudOutboxAsync("synthetic-instance", "synthetic-device")));
        Assert.False(fixture.Vm.IsSyncing);
        Assert.False(fixture.Vm.IsBusy);

        fixture.Vm.DetailTitle = "Edited locally while inhibited";
        Assert.True(await fixture.Vm.SaveSelectedAsync());
        var edited = (await fixture.Store.GetTaskAsync("local"))!;
        Assert.Equal("Edited locally while inhibited", edited.Title);
        Assert.Equal(before.Revision + 1, edited.Revision);
        var after = await fixture.Store.GetPendingPersonalCloudOutboxAsync("synthetic-instance", "synthetic-device");
        Assert.Equal(2, after.Count);
        Assert.Contains(pending, after); // Existing pending intent is retained; only the authorized local edit is appended.
        Assert.Equal(preferences, await File.ReadAllBytesAsync(fixture.SettingsPath));
        Assert.Equal(pairing, await File.ReadAllBytesAsync(fixture.PairingPath));
        Assert.Equal(0, fixture.Backend.VaultReads);
        Assert.Equal(0, fixture.Backend.VaultWrites);
        Assert.Equal(0, fixture.Backend.AuthCalls);
        Assert.Equal(0, fixture.Backend.ProviderFactories);
        Assert.Equal(0, fixture.Backend.ProviderCalls);
        Assert.Equal(0, fixture.Backend.CloudCalls);
        Assert.Equal(0, fixture.Backend.BridgeCalls);
    }

    [Fact]
    public async Task Healthy_dependencies_still_reach_both_providers_and_cloud_for_manual_and_automatic_sync()
    {
        var fixture = await CreateAsync(inhibited: false);
        var preferences = await File.ReadAllBytesAsync(fixture.SettingsPath);
        var pairing = await File.ReadAllBytesAsync(fixture.PairingPath);
        await fixture.Vm.InitializeAsync();
        var before = (await fixture.Store.GetTaskAsync("local"))!;
        await fixture.Vm.RunTodoistSyncAsync();
        await fixture.Vm.RunAutomaticTodoistSyncAsync();
        Assert.True(fixture.Backend.VaultReads >= 2);
        Assert.Equal(0, fixture.Backend.VaultWrites);
        Assert.Equal(2, fixture.Backend.AuthCalls);
        Assert.Equal(4, fixture.Backend.ProviderFactories);
        Assert.Equal(4, fixture.Backend.ProviderCalls);
        Assert.Equal(4, fixture.Backend.CloudCalls);
        Assert.Equal(2, fixture.Backend.BridgeCalls);
        Assert.Equivalent(before, await fixture.Store.GetTaskAsync("local"), strict: true);
        Assert.Equal(preferences, await File.ReadAllBytesAsync(fixture.SettingsPath));
        Assert.Equal(pairing, await File.ReadAllBytesAsync(fixture.PairingPath));
    }

    private async Task<Fixture> CreateAsync(bool inhibited, bool defaultMicrosoftAuth = false)
    {
        Directory.CreateDirectory(_directory);
        var settingsPath = Path.Combine(_directory, "settings.json");
        var pairingPath = Path.Combine(_directory, "personal-cloud-connection.json");
        var preferences = new DesktopPreferencesStore(settingsPath);
        await preferences.SaveAsync(new() { AutomaticSyncEnabled = true, AutomaticRestorePointsEnabled = false,
            ShowGetStarted = false, HasSelectedSpace = true, SelectedSpaceId = SpaceIds.Default, LastView = "Open",
            MicrosoftToDoAccount = new("synthetic-account", "synthetic@example.invalid") });
        var connection = new JsonPersonalCloudConnectionStore(pairingPath);
        await connection.SaveAsync(new() { ServiceOrigin = new("https://synthetic.invalid/"), InstanceId = "synthetic-instance",
            DeviceId = "synthetic-device", ArtifactVersion = "1.0.0", ConnectedAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z") });
        var store = new SqliteTaskStore(Path.Combine(_directory, "tasks.db"));
        await store.InitializeAsync();
        await store.CreateTaskWithPersonalCloudIntentAsync(new() { Id = "local", Title = "Local task" }, "synthetic-instance", "synthetic-device");
        var backend = new Backend();
        var state = new SessionBoundary(inhibited, backend);
        var cloud = new GuardedCloud(state, connection);
        var vm = new MainWindowViewModel(store, new GuardedCredentials(state), preferencesStore: preferences,
            microsoftAuth: defaultMicrosoftAuth ? null : new GuardedMicrosoft(state), personalCloudSetup: cloud,
            todoistSyncProviderFactory: _ => { backend.ProviderFactories++; return new Provider(IntegrationIds.Todoist, backend); },
            microsoftSyncProviderFactory: _ => { backend.ProviderFactories++; return new Provider(IntegrationIds.MicrosoftToDo, backend); });
        return new(store, vm, backend, settingsPath, pairingPath);
    }

    private sealed record Fixture(SqliteTaskStore Store, MainWindowViewModel Vm, Backend Backend, string SettingsPath, string PairingPath);
    private sealed class Backend : ICredentialStore
    {
        public int VaultReads, VaultWrites, AuthGuardAttempts, AuthCalls, ProviderFactories, ProviderCalls, CloudGuardAttempts, CloudCalls, BridgeCalls;
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        { VaultReads++; return Task.FromResult<string?>(key == "todoist-token" ? "synthetic-token" : null); }
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        { VaultWrites++; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        { VaultWrites++; return Task.CompletedTask; }
    }
    private sealed record SessionBoundary(bool Inhibited, Backend Backend);

    private sealed class GuardedCredentials(SessionBoundary state) : ICredentialStore
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            if (state.Inhibited) return Task.FromResult<string?>(null);
            return state.Backend.GetAsync(key, cancellationToken);
        }
        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        { if (state.Inhibited) throw new InvalidOperationException("Synthetic session inhibited"); return state.Backend.SaveAsync(key, value, cancellationToken); }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        { if (state.Inhibited) throw new InvalidOperationException("Synthetic session inhibited"); return state.Backend.RemoveAsync(key, cancellationToken); }
    }

    private sealed class GuardedMicrosoft(SessionBoundary state) : IDesktopMicrosoftAuthService
    {
        public Task<string?> GetTokenAsync(string feature, MicrosoftAccount? account, CancellationToken cancellationToken = default)
        {
            state.Backend.AuthGuardAttempts++;
            if (state.Inhibited) return Task.FromResult<string?>(null);
            state.Backend.AuthCalls++;
            return Task.FromResult<string?>("synthetic-microsoft-token");
        }
        public Task<MicrosoftAccess> ConnectAsync(string feature, Func<SignInCode, Task> showCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DisconnectAsync(string feature) => throw new NotSupportedException();
    }

    private sealed class GuardedCloud(SessionBoundary state, IPersonalCloudConnectionStore connection) : IPersonalCloudSetupService, IPersonalCloudProviderBridgeService
    {
        public bool IsConfigured => true;
        public PersonalCloudConnectionProfile? CurrentConnection => connection.Load();
        public Task<PersonalCloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            state.Backend.CloudGuardAttempts++;
            if (state.Inhibited) throw new InvalidOperationException("Synthetic session inhibited");
            state.Backend.CloudCalls++;
            return Task.FromResult(new PersonalCloudSyncResult(0, 0, 0));
        }
        public Task<PersonalCloudProviderBridgeResult> RunProviderBridgeAsync(IReadOnlyList<PersonalCloudConfiguredProvider> providers, CancellationToken cancellationToken = default)
        { if (state.Inhibited) throw new InvalidOperationException("Synthetic session inhibited"); state.Backend.BridgeCalls++; return Task.FromResult(new PersonalCloudProviderBridgeResult(0, 0, 0, 0)); }
        public Task RetryProviderCommandAsync(string taskId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task OpenAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersonalCloudProvisioningResult> ConnectAsync(CancellationToken cancellationToken = default, IProgress<PersonalCloudSetupProgress>? progress = null) => throw new NotSupportedException();
        public Task<PersonalCloudConnectionProfile> PairExistingAsync(Uri origin, string grant, string deviceName, bool providerBridge, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloudflareD1Export> ExportAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersonalCloudUpgradeResult> UpgradeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Provider(string integrationId, Backend backend) : ISyncProvider
    {
        public string IntegrationId => integrationId;
        public Task<ProviderSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
        { backend.ProviderCalls++; return Task.FromResult(new ProviderSnapshot([], [], [])); }
        public Task CompleteTaskAsync(PendingCompletion completion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected provider write");
    }

    private static T Field<T>(object instance) => (T)instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(x => x.FieldType == typeof(T)).GetValue(instance)!;

    // Decode only operands needed for this constructor-wiring contract; do not depend on compiler-generated field names.
    private static List<(OpCode Code, MemberInfo? Member, int? ArgumentIndex)> ReadInstructions(ConstructorInfo constructor)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(x => x.FieldType == typeof(OpCode))
            .Select(x => (OpCode)x.GetValue(null)!).ToDictionary(x => unchecked((ushort)x.Value));
        var bytes = constructor.GetMethodBody()!.GetILAsByteArray()!;
        var result = new List<(OpCode, MemberInfo?, int?)>();
        for (var offset = 0; offset < bytes.Length;)
        {
            ushort value = bytes[offset++];
            if (value == 0xfe) value = (ushort)(0xfe00 | bytes[offset++]);
            var code = codes[value];
            MemberInfo? member = null;
            if (code.OperandType == OperandType.InlineField || code == OpCodes.Newobj)
                member = constructor.Module.ResolveMember(BitConverter.ToInt32(bytes, offset));
            int? argumentIndex = code == OpCodes.Ldarg_0 ? 0 : code == OpCodes.Ldarg_1 ? 1 : code == OpCodes.Ldarg_2 ? 2 : code == OpCodes.Ldarg_3 ? 3
                : code == OpCodes.Ldarg_S ? bytes[offset] : code == OpCodes.Ldarg ? BitConverter.ToUInt16(bytes, offset) : null;
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4,
            };
            offset += size;
            result.Add((code, member, argumentIndex));
        }
        return result;
    }

    public void Dispose() => TestDirectory.Delete(_directory);
}
