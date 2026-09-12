using System.Text.Json;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.Services;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Desktop.ViewModels;

public sealed record GitHubIssueDraft(string TaskId, string Title, string Body, string DefaultRepository, IReadOnlyList<GitHubRepositoryInfo> Repositories, string? ReplacedLinkId);

public sealed partial class MainWindowViewModel
{
    public TaskExternalLinkInfo? SelectedGitHubLink => _selectedGitHubLink;
    public async Task SignInGitHubAsync(Func<SignInCode, Task> showCode, CancellationToken cancellationToken)
    {
        var clientId = _gitHubIssueService.ResolveClientId();
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("GitHub sign-in is not configured. Use a token instead.");
        var code = await _gitHubIssueService.RequestDeviceCodeAsync(clientId, cancellationToken);
        await showCode(new SignInCode(code.UserCode, code.VerificationUri, "Sign in to GitHub"));
        var token = await _gitHubIssueService.PollForDeviceTokenAsync(clientId, code, cancellationToken);
        await SaveGitHubTokenAsync(token, cancellationToken);
    }

    private async Task<string> RequireGitHubTokenAsync() =>
        await _credentials.GetAsync(GitHubIssueService.TokenKey) is { Length: > 0 } token
            ? token : throw new InvalidOperationException("Connect GitHub in Settings first.");

    public async Task<GitHubIssueDraft?> PrepareGitHubIssueAsync()
    {
        if (!await SaveSelectedAsync() || SelectedTask is not { } selected) return null;
        var task = selected.Task;
        var replacedLink = _selectedGitHubLink?.Id;
        var project = _projects.FirstOrDefault(item => item.Id == task.ProjectId);
        var token = await RequireGitHubTokenAsync();
        var repositories = await _gitHubIssueService.GetRepositoriesAsync(token);
        var connection = (await _store.GetProviderConnectionsAsync()).FirstOrDefault(item => item.IntegrationId == IntegrationIds.GitHub);
        var settings = GitHubIssueService.ReadSettings(connection?.SettingsJson);
        return new GitHubIssueDraft(task.Id, task.Title, GitHubIssueService.BuildIssueBody(task, project),
            settings.DefaultRepositoryFullName, repositories.Where(repo => repo.HasIssues && !repo.IsArchived).ToList(), replacedLink);
    }

    public async Task<IReadOnlyList<GitHubLabelInfo>> GetGitHubLabelsAsync(GitHubRepositoryInfo repository) =>
        await _gitHubIssueService.GetLabelsAsync(await RequireGitHubTokenAsync(), repository.Owner, repository.Name);

    public async Task<string> CreateGitHubIssueAsync(GitHubIssueDraft draft, GitHubIssueCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) throw new InvalidOperationException("Enter an issue title.");
        if (await _store.GetTaskAsync(draft.TaskId) is null) throw new InvalidOperationException("This task no longer exists.");
        var result = await _gitHubIssueService.CreateIssueAsync(await RequireGitHubTokenAsync(), request);
        try
        {
            var link = new TaskExternalLinkInfo
            {
                Id = draft.ReplacedLinkId ?? $"github_{Guid.NewGuid():N}", TaskId = draft.TaskId,
                IntegrationId = IntegrationIds.GitHub, ConnectionId = GitHubIssueService.DefaultConnectionId,
                ExternalId = result.ExternalId, Kind = TaskExternalLinkKinds.Issue, DisplayName = result.DisplayName,
                Url = result.Url, MetadataJson = JsonSerializer.Serialize(new { result.Owner, result.Repository, result.Number, result.State }),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            if (draft.ReplacedLinkId is null) await _store.UpsertTaskExternalLinkAsync(link);
            else await _store.ReplaceTaskExternalLinkAsync(link);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Issue created at {result.Url}, but its local link could not be saved. Do not create it again. {exception.Message}", exception);
        }
        if (SelectedTask?.Task.Id == draft.TaskId) await LoadSelectedGitHubLinkAsync();
        GitHubDefaultRepository = $"{request.Owner}/{request.Repository}";
        if (await SaveGitHubDefaultRepositoryAsync()) StatusMessage = $"GitHub issue {result.DisplayName} created";
        else ReportError($"GitHub issue {result.DisplayName} was created and linked, but the default repository could not be saved. {StatusMessage}");
        return result.Url;
    }

    public async Task RemoveGitHubLinkAsync(TaskExternalLinkInfo link)
    {
        await _store.DeleteTaskExternalLinkAsync(link.Id);
        await LoadSelectedGitHubLinkAsync();
        StatusMessage = "GitHub link removed; the issue is unchanged";
    }

    public async Task<GitHubIssueStatusResult> CheckGitHubLinkAsync(TaskExternalLinkInfo link)
    {
        var separator = link.ExternalId.LastIndexOf('#');
        if (separator < 0 || !TryParseRepository(link.ExternalId[..separator], out var owner, out var repository) ||
            !int.TryParse(link.ExternalId[(separator + 1)..], out var number))
            return new GitHubIssueStatusResult(false, true, "This link cannot be checked.");
        return await _gitHubIssueService.GetIssueStatusAsync(await RequireGitHubTokenAsync(), owner, repository, number);
    }
}
