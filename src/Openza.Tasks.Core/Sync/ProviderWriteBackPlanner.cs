using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Core.Sync;

public static class ProviderWriteBackPlanner
{
    public static PendingCompletion? CreateCompletion(TaskItem task, bool completed, DateTimeOffset now)
    {
        if (!HasWritableProviderIdentity(task))
        {
            return null;
        }

        return new PendingCompletion
        {
            Id = $"completion_{task.Id}_{now.ToUnixTimeMilliseconds()}",
            TaskId = task.Id,
            Provider = task.SourceIntegrationId ?? task.IntegrationId,
            ProviderTaskId = BuildProviderTaskId(task),
            Completed = completed,
            CompletedAt = completed ? now : null,
            CreatedAt = now,
        };
    }

    public static PendingTaskDateUpdate? CreateTodoistDateUpdate(TaskItem original, TaskItem updated, DateTimeOffset now)
    {
        var provider = original.SourceIntegrationId ?? original.IntegrationId;
        if (!string.Equals(provider, IntegrationIds.Todoist, StringComparison.Ordinal) ||
            !HasWritableProviderIdentity(original) ||
            !string.IsNullOrWhiteSpace(original.RecurrenceRule) ||
            (original.PlannedOn == updated.PlannedOn && NullableDateTimesEqual(original.PlannedAt, updated.PlannedAt)))
        {
            return null;
        }

        return new PendingTaskDateUpdate
        {
            Id = $"date_{updated.Id}_{now.ToUnixTimeMilliseconds()}",
            TaskId = updated.Id,
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = BuildProviderTaskId(original),
            PlannedOn = updated.PlannedOn,
            PlannedAt = updated.PlannedAt,
            CreatedAt = now,
        };
    }

    public static DateTimeOffset? PreserveExactTime(
        DateOnly? selectedDate,
        DateOnly? existingDate,
        DateTimeOffset? existingDateTime)
    {
        if (selectedDate is null || existingDateTime is null)
        {
            return null;
        }

        var exactDate = TaskDateValues.FromDateTimeOffset(existingDateTime);
        return existingDate == selectedDate && exactDate == selectedDate ? existingDateTime : null;
    }

    private static string BuildProviderTaskId(TaskItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.SourceProviderTaskId))
        {
            return task.SourceProviderTaskId;
        }

        if (task.IntegrationId == IntegrationIds.MicrosoftToDo &&
            task.ProjectId?.StartsWith("mstodo_", StringComparison.Ordinal) == true)
        {
            return $"{task.ProjectId["mstodo_".Length..]}|{task.ExternalId}";
        }

        return task.ExternalId ?? task.Id;
    }

    private static bool HasWritableProviderIdentity(TaskItem task) => task.IsProviderTask
        ? !string.IsNullOrWhiteSpace(task.ExternalId)
        : !string.IsNullOrWhiteSpace(task.SourceIntegrationId) &&
          (!string.IsNullOrWhiteSpace(task.SourceProviderTaskId) || !string.IsNullOrWhiteSpace(task.SourceExternalId));

    private static bool NullableDateTimesEqual(DateTimeOffset? left, DateTimeOffset? right) =>
        left?.ToUnixTimeSeconds() == right?.ToUnixTimeSeconds();
}
