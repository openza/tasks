using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudConnectionTests
{
    [Fact]
    public async Task Connection_profile_survives_restart_without_containing_credentials()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openza-personal-cloud-connection-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "connection.json");
        try
        {
            var store = new JsonPersonalCloudConnectionStore(path);
            var expected = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new Uri("https://openza-personal-test.workers.dev/"),
                InstanceId = "instance_personal",
                DeviceId = "device_1234567890",
                ArtifactVersion = "1.2.3",
                ConnectedAt = DateTimeOffset.Parse("2026-09-14T00:00:00Z"),
            };

            await store.SaveAsync(expected);

            Assert.Equal(expected, new JsonPersonalCloudConnectionStore(path).Load());
            var raw = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("credential", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("grant", raw, StringComparison.OrdinalIgnoreCase);

            await store.ClearAsync();
            Assert.Null(store.Load());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Connection_profile_rejects_insecure_or_fragmented_origins()
    {
        var store = new JsonPersonalCloudConnectionStore(Path.Combine(Path.GetTempPath(), $"openza-invalid-connection-{Guid.NewGuid():N}.json"));
        var invalid = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri("http://example.test/#secret"),
            InstanceId = "instance_personal",
            DeviceId = "device_1234567890",
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.SaveAsync(invalid));
        Assert.Equal("connection_invalid", error.Code);
    }
}
