using System.ComponentModel;
using System.Diagnostics;
using Openza.Tasks.Core.Credentials;

namespace Openza.Tasks.Desktop.Services;

public sealed class SecretToolCredentialStore : ICredentialStore
{
    private const string ApplicationAttribute = "openza-app";
    private const string ApplicationValue = "tasks";
    private const string KeyAttribute = "credential-key";

    public async Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var result = await RunAsync(
            ["store", "--label=Openza Tasks", ApplicationAttribute, ApplicationValue, KeyAttribute, key],
            value,
            cancellationToken);
        EnsureSuccess(result, "save the credential");
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var result = await RunAsync(
            ["lookup", ApplicationAttribute, ApplicationValue, KeyAttribute, key],
            null,
            cancellationToken);
        return result.ExitCode == 0 ? NullIfEmpty(result.StandardOutput) : null;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var result = await RunAsync(
            ["clear", ApplicationAttribute, ApplicationValue, KeyAttribute, key],
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

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var detail = NullIfEmpty(result.StandardError) ?? "Secret Service returned an error.";
        throw new InvalidOperationException($"Could not {operation}: {detail}");
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
