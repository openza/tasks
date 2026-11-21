using System.ComponentModel;
using System.Diagnostics;

namespace Openza.Tasks.Core.Credentials;

public sealed class SecretToolCredentialStore : ICredentialStore
{
    private const string ApplicationAttribute = "openza-app";
    private const string KeyAttribute = "credential-key";
    private readonly string _applicationValue;
    private readonly string _label;
    private readonly Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> _run;
    private readonly Func<string, string?> _environment;

    public SecretToolCredentialStore(string applicationValue, string label)
        : this(applicationValue, label, RunAsync, Environment.GetEnvironmentVariable)
    {
    }

    internal SecretToolCredentialStore(string applicationValue, string label,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> run,
        Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _applicationValue = applicationValue;
        _label = label;
        _run = run;
        _environment = environment;
    }

    public async Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var result = await _run(
            ["store", $"--label={_label}", ApplicationAttribute, _applicationValue, KeyAttribute, key],
            value,
            cancellationToken);
        EnsureSuccess(result, "save the credential");
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var result = await _run(
            ["lookup", ApplicationAttribute, _applicationValue, KeyAttribute, key],
            null,
            cancellationToken);
        return result.ExitCode == 0 ? NullIfEmpty(result.StandardOutput) : null;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var result = await _run(
            ["clear", ApplicationAttribute, _applicationValue, KeyAttribute, key],
            null,
            cancellationToken);
        if (result.ExitCode is not 0 and not 1)
        {
            EnsureSuccess(result, "remove the credential");
        }
    }

    private static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "secret-tool",
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Secret Service could not be started.");
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "Ubuntu Secret Service tooling is missing. Install the libsecret-tools package before connecting providers.",
                exception);
        }
    }

    private void ThrowIfSnapKeyringError(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0 || NullIfEmpty(_environment("SNAP_NAME")) is not { } snapName) return;

        var error = result.StandardError;
        var instance = NullIfEmpty(_environment("SNAP_INSTANCE_NAME")) ?? snapName;
        var command = $"sudo snap connect {instance}:password-manager-service";
        string? guidance = null;
        if (error.Contains("AppArmor", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase))
        {
            guidance = $"Your desktop does not provide the Secret portal, so this Snap needs keyring access. Run:\n\n{command}\n\nThen try again.";
        }
        else if (error.Contains("org.freedesktop.secrets", StringComparison.OrdinalIgnoreCase) &&
                 (error.Contains("was not provided by any .service files", StringComparison.OrdinalIgnoreCase) ||
                  error.Contains("ServiceUnknown", StringComparison.OrdinalIgnoreCase)))
        {
            guidance = $"No keyring (Secret Service, such as GNOME Keyring or KWallet) is running or available. Install or start one. If your desktop has no Secret portal, also run:\n\n{command}\n\nThen try again.";
        }

        if (guidance is not null) throw new InvalidOperationException($"Could not {operation}: {guidance}");
    }

    private void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        ThrowIfSnapKeyringError(result, operation);
        var detail = NullIfEmpty(result.StandardError) ?? "Secret Service returned an error.";
        throw new InvalidOperationException($"Could not {operation}: {detail}");
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
