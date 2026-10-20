using Openza.Tasks.Core.PersonalCloud;
using System.Text.Json.Nodes;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudConnectionTests
{
    [Theory]
    [InlineData("serviceOrigin")]
    [InlineData("instanceId")]
    [InlineData("deviceId")]
    [InlineData("artifactVersion")]
    public async Task Null_required_json_fields_are_contract_errors_without_replacing_source(string field)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openza-null-connection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "connection.json");
        try
        {
            var json = new JsonObject
            {
                ["serviceOrigin"] = "https://example.invalid/", ["instanceId"] = "instance-test",
                ["deviceId"] = "device-test", ["artifactVersion"] = "synthetic",
                ["connectedAt"] = "2026-10-18T00:00:00Z",
            };
            json[field] = null;
            var original = json.ToJsonString();
            await File.WriteAllTextAsync(path, original);
            var store = new JsonPersonalCloudConnectionStore(path);
            var loadError = Assert.Throws<PersonalCloudK0ContractException>(() => store.Load());
            Assert.Equal("connection_invalid", loadError.Code);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            var invalid = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = field == "serviceOrigin" ? null! : new("https://example.invalid/"),
                InstanceId = field == "instanceId" ? null! : "instance-test",
                DeviceId = field == "deviceId" ? null! : "device-test",
                ArtifactVersion = field == "artifactVersion" ? null! : "synthetic",
                ConnectedAt = DateTimeOffset.UtcNow,
            };
            var saveError = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.SaveAsync(invalid));
            Assert.Equal("connection_invalid", saveError.Code);
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Theory]
    [InlineData("https://openza-personal-test.workers.dev/")]
    [InlineData("http://localhost:8787/")]
    [InlineData("http://127.0.0.1:8787/")]
    [InlineData("http://[::1]:8787/")]
    public async Task Connection_profile_survives_restart_without_containing_credentials(string origin)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"openza-personal-cloud-connection-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "connection.json");
        try
        {
            var store = new JsonPersonalCloudConnectionStore(path);
            var expected = new PersonalCloudConnectionProfile
            {
                ServiceOrigin = new Uri(origin),
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

    [Theory]
    [InlineData("http://example.test/")]
    [InlineData("http://192.168.1.2:8787/")]
    [InlineData("http://localhost.example.test/")]
    [InlineData("http://localhost:8787/#secret")]
    [InlineData("http://localhost:8787/?token=secret")]
    [InlineData("http://localhost:8787/path")]
    public async Task Connection_profile_rejects_insecure_or_fragmented_origins(string origin)
    {
        var store = new JsonPersonalCloudConnectionStore(Path.Combine(Path.GetTempPath(), $"openza-invalid-connection-{Guid.NewGuid():N}.json"));
        var invalid = new PersonalCloudConnectionProfile
        {
            ServiceOrigin = new Uri(origin),
            InstanceId = "instance_personal",
            DeviceId = "device_1234567890",
            ArtifactVersion = "1.2.3",
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        var error = await Assert.ThrowsAsync<PersonalCloudK0ContractException>(() => store.SaveAsync(invalid));
        Assert.Equal("connection_invalid", error.Code);
    }
}
