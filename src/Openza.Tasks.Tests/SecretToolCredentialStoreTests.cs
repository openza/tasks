using Openza.Tasks.Core.Credentials;
using Xunit;

namespace Openza.Tasks.Tests;

public sealed class SecretToolCredentialStoreTests
{
    [Theory]
    [InlineData("save", "secret-tool: An AppArmor policy prevents this sender from sending this message", null)]
    [InlineData("save", "secret-tool: An AppArmor policy prevents this sender from sending this message", "openza-tasks_test")]
    [InlineData("remove", "secret-tool: An AppArmor policy prevents this sender from sending this message", "")]
    public async Task SnapDeniedErrorsExplainConnection(string operation, string error, string? instance)
    {
        var store = Create(1, "", error, "openza-tasks", instance);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, operation));
        Assert.Contains("If password-manager-service is not connected", exception.Message);
        Assert.Contains("If it is already connected", exception.Message);
        Assert.Contains($"sudo snap connect {(string.IsNullOrEmpty(instance) ? "openza-tasks" : instance)}:password-manager-service", exception.Message);
        Assert.Contains("try again", exception.Message);
        Assert.Contains($"Details: {error}", exception.Message);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("remove")]
    public async Task SnapMissingKeyringExplainsInstallation(string operation)
    {
        const string error = "secret-tool: The name org.freedesktop.secrets was not provided by any .service files";
        var store = Create(1, "", error, "openza-tasks");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, operation));
        Assert.Contains("GNOME Keyring or KWallet", exception.Message);
        Assert.Contains("Install or start one", exception.Message);
        Assert.Contains("If your desktop has no Secret portal", exception.Message);
        Assert.Contains("sudo snap connect openza-tasks:password-manager-service", exception.Message);
        Assert.Contains($"Details: {error}", exception.Message);
    }

    [Theory]
    [InlineData("secret-tool: AppArmor AccessDenied")]
    [InlineData("secret-tool: The name org.freedesktop.secrets was not provided by any .service files")]
    public async Task SnapLookupFailuresStayQuiet(string error)
    {
        Assert.Null(await Create(1, "", error, "openza-tasks").GetAsync("key"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task OutsideSnapKeepsExistingBehavior(string? snap)
    {
        const string error = "secret-tool: AppArmor AccessDenied";
        var store = Create(1, "", error, snap, "ignored");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("key", "value"));
        Assert.Equal($"Could not save the credential: {error}", exception.Message);
        Assert.Null(await store.GetAsync("key"));
        exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync("key"));
        Assert.Equal($"Could not remove the credential: {error}", exception.Message);
    }

    [Fact]
    public async Task SnapSuccessAndMissingCredentialsRemainNormal()
    {
        var store = Create(0, " token\n", "", "openza-tasks");
        await store.SaveAsync("key", "value");
        Assert.Equal("token", await store.GetAsync("key"));
        await store.RemoveAsync("key");
        Assert.Null(await Create(0, "", "", "openza-tasks").GetAsync("key"));
        store = Create(1, "", "", "openza-tasks");
        Assert.Null(await store.GetAsync("key"));
        await store.RemoveAsync("key");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("key", "value"));
        Assert.Equal("Could not save the credential: Secret Service returned an error.", exception.Message);
    }

    [Fact]
    public async Task UnknownSnapErrorsKeepExistingBehavior()
    {
        var store = Create(1, "", "other failure", "openza-tasks");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RemoveAsync("key"));
        Assert.Equal("Could not remove the credential: other failure", exception.Message);
        Assert.Null(await store.GetAsync("key"));
    }

    [Theory]
    [InlineData("save", "secret-tool: org.freedesktop.DBus.Error.AccessDenied: User dismissed the prompt")]
    [InlineData("remove", "secret-tool: org.freedesktop.DBus.Error.AccessDenied: User dismissed the prompt")]
    [InlineData("remove", "secret-tool: Could not connect: No such file or directory\n")]
    public async Task OtherErrorsRetainDetailsWithoutConnectionAdvice(string operation, string error)
    {
        var store = Create(1, "", error, "openza-tasks");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, operation));
        Assert.Contains(error.Trim(), exception.Message);
        Assert.DoesNotContain("snap connect", exception.Message);
        Assert.DoesNotContain("Secret portal", exception.Message);
    }

    private static Task Run(SecretToolCredentialStore store, string operation) => operation switch
    {
        "save" => store.SaveAsync("key", "value"),
        "lookup" => store.GetAsync("key"),
        _ => store.RemoveAsync("key"),
    };

    private static SecretToolCredentialStore Create(int exitCode, string output, string error, string? snap, string? instance = null) =>
        new("test", "Test", (_, _, _) => Task.FromResult(new SecretToolCredentialStore.ProcessResult(exitCode, output, error)),
            name => name == "SNAP_NAME" ? snap : name == "SNAP_INSTANCE_NAME" ? instance : null);
}
