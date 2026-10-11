using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudNotesDeadlineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-personal-cloud-notes", Guid.NewGuid().ToString("N"));
    private const string InstanceId = "instance_00000000000000000000000000000001";
    private const string DeviceId = "device_notes_replica_0000000000000001";

    [Fact]
    public async Task Replica_and_bootstrap_preserve_notes_and_deadline()
    {
        Directory.CreateDirectory(_directory);
        var store = new SqliteTaskStore(Path.Combine(_directory, "replica.db"));
        await store.InitializeAsync();
        var page = """
        {"protocolVersion":1,"product":"tasks","changes":[
          {"protocolVersion":1,"product":"tasks","changeSeq":1,"mutationId":"notes-create","operation":"task.created","entityKind":"task","entityId":"notes-task","entityVersion":1,"payload":{"task":{"id":"notes-task","spaceId":"space_default","title":"Notes","completionState":"open","workflowStatus":"inbox","createdAt":"2026-09-15T05:30:00.000Z"}},"serverAcceptedAt":"2026-09-15T05:30:01.000Z"},
          {"protocolVersion":1,"product":"tasks","changeSeq":2,"mutationId":"notes-update","operation":"task.updatedOpenzaFields","entityKind":"task","entityId":"notes-task","entityVersion":2,"payload":{"task":{"id":"notes-task","notes":"Shared detail"}},"serverAcceptedAt":"2026-09-15T05:30:02.000Z"},
          {"protocolVersion":1,"product":"tasks","changeSeq":3,"mutationId":"deadline-update","operation":"task.updatedOpenzaFields","entityKind":"task","entityId":"notes-task","entityVersion":3,"payload":{"task":{"id":"notes-task","deadlineOn":"2026-09-22"}},"serverAcceptedAt":"2026-09-15T05:30:03.000Z"}
        ],"nextCheckpoint":3}
        """;
        Assert.Equal(3, await store.ApplyPersonalCloudChangePageAsync(InstanceId, DeviceId, page));
        var task = await store.GetTaskAsync("notes-task");
        Assert.NotNull(task);
        Assert.Equal("Shared detail", task.Notes);
        Assert.Equal(new DateOnly(2026, 9, 22), task.DeadlineOn);

        var bootstrap = """
        {"protocolVersion":1,"product":"tasks","checkpoint":4,"snapshot":{"spaces":[{"id":"space_default","name":"Inbox","deletedAt":null}],"projects":[],"tasks":[{"id":"boot-task","spaceId":"space_default","projectId":null,"parentId":null,"title":"Boot","completionState":"open","workflowStatus":"inbox","priority":2,"plannedOn":null,"plannedAt":null,"notes":"From bootstrap","deadlineOn":"2026-09-23","completedAt":null,"createdAt":"2026-09-15T05:30:00.000Z","entityVersion":2,"deletedAt":null}],"labels":[],"taskLabels":[],"tombstones":[]}}
        """;
        Assert.Equal(4, await store.ReplacePersonalCloudReplicaFromBootstrapAsync(InstanceId, DeviceId, bootstrap));
        var boot = await store.GetTaskAsync("boot-task");
        Assert.NotNull(boot);
        Assert.Equal("From bootstrap", boot.Notes);
        Assert.Equal(new DateOnly(2026, 9, 23), boot.DeadlineOn);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestDirectory.Delete(_directory);
    }
}
