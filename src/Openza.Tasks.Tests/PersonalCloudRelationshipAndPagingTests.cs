using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudRelationshipAndPagingTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"openza-personal-cloud-relationships",Guid.NewGuid().ToString("N"));
    private const string Instance="instance_00000000000000000000000000000001",Device="device_relationship_replica";

    [Fact]
    public async Task Applies_a_bounded_multi_change_page_atomically_with_relationships()
    {
        Directory.CreateDirectory(_directory);var store=new SqliteTaskStore(Path.Combine(_directory,"replica.db"));await store.InitializeAsync();
        var json="""
        {"protocolVersion":1,"product":"tasks","changes":[
          {"protocolVersion":1,"product":"tasks","changeSeq":1,"mutationId":"m1","operation":"task.created","entityKind":"task","entityId":"child","entityVersion":1,"payload":{"task":{"id":"child","spaceId":"space_default","title":"Child","completionState":"open","workflowStatus":"inbox","createdAt":"2026-09-13T07:00:00.000Z"}},"serverAcceptedAt":"2026-09-13T07:00:01.000Z"},
          {"protocolVersion":1,"product":"tasks","changeSeq":2,"mutationId":"m2","operation":"task.projectSet","entityKind":"task","entityId":"child","entityVersion":2,"payload":{"task":{"id":"child","projectId":"project1","project":{"id":"project1","name":"Launch"}}},"serverAcceptedAt":"2026-09-13T07:00:02.000Z"},
          {"protocolVersion":1,"product":"tasks","changeSeq":3,"mutationId":"m3","operation":"task.moved","entityKind":"task","entityId":"child","entityVersion":3,"payload":{"task":{"id":"child","spaceId":"space2","space":{"id":"space2","name":"Personal"},"projectId":null,"parentId":null}},"serverAcceptedAt":"2026-09-13T07:00:03.000Z"},
          {"protocolVersion":1,"product":"tasks","changeSeq":4,"mutationId":"m4","operation":"task.labelAdded","entityKind":"task","entityId":"child","entityVersion":4,"payload":{"task":{"id":"child","label":{"id":"label1","name":"Focus"}}},"serverAcceptedAt":"2026-09-13T07:00:04.000Z"},
          {"protocolVersion":1,"product":"tasks","changeSeq":5,"mutationId":"m5","operation":"task.labelRemoved","entityKind":"task","entityId":"child","entityVersion":5,"payload":{"task":{"id":"child","labelId":"label1"}},"serverAcceptedAt":"2026-09-13T07:00:05.000Z"},
          {"protocolVersion":1,"product":"tasks","changeSeq":6,"mutationId":"m6","operation":"task.created","entityKind":"task","entityId":"parent","entityVersion":1,"payload":{"task":{"id":"parent","spaceId":"space2","title":"Parent","completionState":"open","workflowStatus":"inbox","createdAt":"2026-09-13T07:00:00.000Z"}},"serverAcceptedAt":"2026-09-13T07:00:06.000Z"},
          {"protocolVersion":1,"product":"tasks","changeSeq":7,"mutationId":"m7","operation":"task.parentSet","entityKind":"task","entityId":"child","entityVersion":6,"payload":{"task":{"id":"child","parentId":"parent"}},"serverAcceptedAt":"2026-09-13T07:00:07.000Z"}
        ],"nextCheckpoint":7}
        """;
        Assert.Equal(7,await store.ApplyPersonalCloudChangePageAsync(Instance,Device,json));
        var child=await store.GetTaskAsync("child");Assert.NotNull(child);Assert.Equal("space2",child.SpaceId);Assert.Null(child.ProjectId);Assert.Equal("parent",child.ParentId);Assert.Empty(child.Labels);
        Assert.Equal(7,await store.ApplyPersonalCloudChangePageAsync(Instance,Device,json));

        var broken=json.Replace("\"mutationId\":\"m7\"","\"mutationId\":\"mismatch\"",StringComparison.Ordinal);
        await Assert.ThrowsAsync<PersonalCloudK0ContractException>(()=>store.ApplyPersonalCloudChangePageAsync(Instance,Device,broken));
        Assert.Equal(7,await store.GetPersonalCloudCheckpointAsync(Instance,Device));
    }

    [Fact]
    public async Task Source_relationship_edits_and_outbox_advance_together()
    {
        Directory.CreateDirectory(_directory);var store=new SqliteTaskStore(Path.Combine(_directory,"source.db"));await store.InitializeAsync();
        await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem{Id="child",SpaceId=SpaceIds.Default,Title="Child"},Instance,Device);
        await store.MutateTaskRelationshipWithPersonalCloudIntentAsync("child","task.setProject","project1","Launch",1,Instance,Device);
        await store.MutateTaskRelationshipWithPersonalCloudIntentAsync("child","task.move","space2","Personal",2,Instance,Device);
        await store.MutateTaskRelationshipWithPersonalCloudIntentAsync("child","task.addLabel","label1","Focus",3,Instance,Device);
        await store.MutateTaskRelationshipWithPersonalCloudIntentAsync("child","task.removeLabel","label1",null,4,Instance,Device);
        await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem{Id="parent",SpaceId="space2",Title="Parent"},Instance,Device);
        var final=await store.MutateTaskRelationshipWithPersonalCloudIntentAsync("child","task.setParent","parent",null,5,Instance,Device);
        Assert.Equal(7,final.DeviceSequence);Assert.Equal(7,(await store.GetPendingPersonalCloudOutboxAsync(Instance,Device)).Count);
        var child=await store.GetTaskAsync("child");Assert.NotNull(child);Assert.Equal("space2",child.SpaceId);Assert.Null(child.ProjectId);Assert.Equal("parent",child.ParentId);Assert.Empty(child.Labels);
    }

    [Fact]
    public async Task Bootstrap_is_repeatable_and_replaces_only_the_tracked_cloud_replica()
    {
        Directory.CreateDirectory(_directory);var store=new SqliteTaskStore(Path.Combine(_directory,"bootstrap.db"));await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem{Id="local-unrelated",Title="Keep local",SpaceId=SpaceIds.Default});
        var json="""
        {"protocolVersion":1,"product":"tasks","checkpoint":9,"snapshot":{"spaces":[{"id":"space_default","name":"Inbox","deletedAt":null}],"projects":[],"tasks":[{"id":"cloud-task","spaceId":"space_default","projectId":null,"parentId":null,"title":"Cloud","completionState":"open","workflowStatus":"inbox","priority":2,"plannedOn":null,"plannedAt":null,"completedAt":null,"createdAt":"2026-09-13T07:00:00.000Z","entityVersion":1,"deletedAt":null}],"labels":[],"taskLabels":[],"tombstones":[]}}
        """;
        Assert.Equal(9,await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance,Device,json));
        Assert.Equal(9,await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance,Device,json));
        Assert.NotNull(await store.GetTaskAsync("cloud-task"));Assert.NotNull(await store.GetTaskAsync("local-unrelated"));
        var empty="""
        {"protocolVersion":1,"product":"tasks","checkpoint":10,"snapshot":{"spaces":[{"id":"space_default","name":"Inbox","deletedAt":null}],"projects":[],"tasks":[],"labels":[],"taskLabels":[],"tombstones":[]}}
        """;
        Assert.Equal(10,await store.ReplacePersonalCloudReplicaFromBootstrapAsync(Instance,Device,empty));
        Assert.Null(await store.GetTaskAsync("cloud-task"));Assert.NotNull(await store.GetTaskAsync("local-unrelated"));
    }

    public void Dispose()=>TestDirectory.Delete(_directory);
}
