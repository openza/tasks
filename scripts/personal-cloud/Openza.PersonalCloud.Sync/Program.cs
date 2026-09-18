using Openza.Tasks.Core.Credentials;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;

if (args.Length != 2 || !Uri.TryCreate(args[0], UriKind.Absolute, out var origin))
{
    Console.Error.WriteLine("Usage: Openza.PersonalCloud.Sync <https-origin> <dev-data-directory>");
    return 2;
}

var dataDirectory = Path.GetFullPath(args[1]);
var databasePath = Path.Combine(dataDirectory, "openza-tasks.db");
if (!File.Exists(databasePath))
{
    Console.Error.WriteLine("The Openza Tasks database was not found.");
    return 2;
}

var credentials = new SecretToolCredentialStore("tasks-dev", "Openza Tasks Dev");
var identity = new PersonalCloudDeviceIdentityClient(new HttpClient(), credentials);
var storedDevice = await identity.GetStoredDeviceAsync(origin);
var profile = new PersonalCloudConnectionProfile
{
    ServiceOrigin = storedDevice.ServiceOrigin,
    InstanceId = storedDevice.InstanceId,
    DeviceId = storedDevice.DeviceId,
    ArtifactVersion = "0.1.0-preview.1",
    ConnectedAt = DateTimeOffset.UtcNow,
};
var store = new SqliteTaskStore(databasePath);
await store.InitializeAsync();
var sync = new PersonalCloudSyncClient(new HttpClient(), identity, store, profile);
await new JsonPersonalCloudConnectionStore(Path.Combine(dataDirectory, "personal-cloud-connection.json"))
    .SaveAsync(profile);

var first = await sync.SyncAsync();
var staged = await store.StageUnpublishedTasksForPersonalCloudAsync(profile.InstanceId, profile.DeviceId);
var pushed = first.PushedMutations;
var pulled = first.PulledChanges;
var checkpoint = first.Checkpoint;
while ((await store.GetPendingPersonalCloudOutboxAsync(profile.InstanceId, profile.DeviceId)).Count > 0)
{
    var pass = await sync.SyncAsync();
    pushed += pass.PushedMutations;
    pulled += pass.PulledChanges;
    checkpoint = pass.Checkpoint;
    Console.WriteLine($"Progress: {pushed} mutations acknowledged; checkpoint {checkpoint}.");
}

Console.WriteLine($"Existing cloud changes received: {first.PulledChanges}");
Console.WriteLine($"Local changes staged: {staged}");
Console.WriteLine($"Local changes uploaded: {pushed}");
Console.WriteLine($"Cloud changes applied: {pulled}");
Console.WriteLine($"Saved checkpoint: {checkpoint}");
return 0;
