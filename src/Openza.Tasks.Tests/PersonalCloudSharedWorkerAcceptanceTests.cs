using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

// Explicitly opt in against the disposable local acceptance fixture only.
public sealed class PersonalCloudSharedWorkerAcceptanceTests
{
    [SharedWorkerFact]
    [Trait("Category", "LocalWorkerAcceptance")]
    public async Task Shared_task_round_trip_preserves_edits_moves_completion_and_deletion_across_restart()
    {
        var origin = new Uri(Environment.GetEnvironmentVariable("OPENZA_SHARED_ACCEPTANCE_ORIGIN")!);
        Assert.True(origin.IsLoopback && origin.Scheme == "http");
        using var web = new HttpClient { BaseAddress = origin };
        var fixture = await web.GetFromJsonAsync<JsonElement>("/__synthetic-fixture");
        Assert.Equal("openza-shared-parity", fixture.GetProperty("fixture").GetString());
        web.DefaultRequestHeaders.Add("Origin", "https://personal-cloud.local");
        web.DefaultRequestHeaders.Add("x-openza-csrf", "synthetic-csrf");
        using var http = new HttpClient { BaseAddress = origin };
        var credentials = new InMemoryCredentialStore();
        var identity = new PersonalCloudDeviceIdentityClient(http, credentials);
        using var pairing = await web.PostAsJsonAsync("/auth/pairing-grants", new { });
        pairing.EnsureSuccessStatusCode();
        var grant = await pairing.Content.ReadFromJsonAsync<JsonElement>();
        var paired = await identity.PairAsync(origin, grant.GetProperty("grant").GetString()!, "Synthetic shared acceptance", false);
        var profile = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = origin, InstanceId = paired.InstanceId, DeviceId = paired.DeviceId,
            ArtifactVersion = "synthetic-test", ConnectedAt = DateTimeOffset.UtcNow,
        };
        var directory = Path.Combine(Path.GetTempPath(), "openza-shared-acceptance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "replica.db");
            var store = new SqliteTaskStore(path); await store.InitializeAsync();
            var sync = new PersonalCloudSyncClient(http, identity, store, profile);
            async Task DrainAsync()
            {
                for (var pass = 0; pass < 20; pass++)
                {
                    Assert.Null((await sync.SyncAsync()).BlockedCode);
                    if ((await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId)).Count == 0) return;
                }
                Assert.Fail("Disposable shared outbox did not drain.");
            }
            await DrainAsync();
            var target = new SpaceItem { Id = $"space_{Guid.NewGuid():N}", Name = "Shared destination" };
            await store.UpsertSpaceWithPersonalCloudIntentAsync(target, profile.InstanceId, profile.DeviceId);
            await DrainAsync();
            var task = new TaskItem
            {
                Id = $"task_{Guid.NewGuid():N}", SpaceId = SpaceIds.Default,
                Title = "Desktop shared round trip ✓", Notes = "Desktop notes", Priority = 1,
                PlannedOn = new DateOnly(2026, 10, 19), DeadlineOn = new DateOnly(2026, 10, 20),
            };
            await store.CreateRichTaskWithPersonalCloudIntentAsync(task, profile.InstanceId, profile.DeviceId);
            await DrainAsync();
            var actor = $"web_{Guid.NewGuid():N}"; long sequence = 0;
            async Task<JsonElement> ReadTaskAsync()
            {
                var payload = await web.GetFromJsonAsync<JsonElement>("/v1/tasks");
                return payload.GetProperty("tasks").EnumerateArray().Single(item => item.GetProperty("id").GetString() == task.Id).Clone();
            }
            async Task PostAsync(string operation, long version, object payload)
            {
                using var response = await web.PostAsJsonAsync("/v1/tasks/mutations", new
                {
                    protocolVersion = 1, product = "tasks", instanceId = profile.InstanceId,
                    deviceId = actor, deviceSequence = ++sequence, mutationId = $"mutation_{Guid.NewGuid():N}",
                    operation, entityId = task.Id, baseEntityVersion = version, payload,
                    clientCreatedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow),
                });
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }
            var current = await ReadTaskAsync();
            Assert.Equal(task.Title, current.GetProperty("title").GetString());
            Assert.Equal("2026-10-20", current.GetProperty("deadlineOn").GetString());
            Assert.Equal(JsonValueKind.String, current.GetProperty("updatedAt").ValueKind);
            await PostAsync("task.patch", current.GetProperty("entityVersion").GetInt64(), new { fields = new { notes = "Web notes नमस्ते", priority = 2 } });
            await DrainAsync();
            // Reopen the actual SQLite store and the sync client using the same paired identity.
            store = new SqliteTaskStore(path); await store.InitializeAsync();
            sync = new PersonalCloudSyncClient(http, identity, store, profile);
            var pulled = (await store.GetTaskAsync(task.Id))!;
            Assert.Equal("Web notes नमस्ते", pulled.Notes); Assert.Equal(2, pulled.Priority);
            Assert.NotNull(pulled.UpdatedAt);
            await store.PatchTaskWithPersonalCloudIntentAsync(task.Id, new Dictionary<string, object?> { ["title"] = "Desktop after restart" }, pulled.Revision, profile.InstanceId, profile.DeviceId);
            await DrainAsync(); current = await ReadTaskAsync();
            Assert.Equal("Desktop after restart", current.GetProperty("title").GetString());
            await PostAsync("task.moveTree", current.GetProperty("entityVersion").GetInt64(), new { spaceId = target.Id, fields = new { } });
            await DrainAsync(); Assert.Equal(target.Id, (await store.GetTaskAsync(task.Id))!.SpaceId);
            current = await ReadTaskAsync();
            await PostAsync("task.complete", current.GetProperty("entityVersion").GetInt64(), new { completedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow) });
            await DrainAsync(); Assert.True((await store.GetTaskAsync(task.Id))!.IsCompleted);
            var version = (await store.GetPersonalCloudEntityVersionAsync(profile.InstanceId, task.Id))!.Value;
            await store.ReopenTaskWithPersonalCloudIntentAsync(task.Id, DateTimeOffset.UtcNow, version, profile.InstanceId, profile.DeviceId);
            await DrainAsync(); current = await ReadTaskAsync();
            Assert.Equal("open", current.GetProperty("completionState").GetString());
            await PostAsync("task.delete", current.GetProperty("entityVersion").GetInt64(), new { deletedAt = PersonalCloudK0Mapper.FormatTimestamp(DateTimeOffset.UtcNow) });
            await DrainAsync(); Assert.Null(await store.GetTaskAsync(task.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SharedWorkerFactAttribute : FactAttribute
    {
        public SharedWorkerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENZA_SHARED_ACCEPTANCE_ORIGIN")))
                Skip = "Opt-in shared round trip against the disposable local Worker fixture.";
        }
    }
}
