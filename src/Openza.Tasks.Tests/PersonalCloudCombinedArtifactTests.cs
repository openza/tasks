using System.Security.Cryptography;
using System.Text;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudCombinedArtifactTests
{
    [CombinedArtifactAcceptance]
    public void Combined_bundle_and_Desktop_copied_bundle_load_through_verified_setup_artifact_contract()
    {
        var source = Environment.GetEnvironmentVariable("OPENZA_COMBINED_ARTIFACT_DIRECTORY")!;
        var copied = Environment.GetEnvironmentVariable("OPENZA_COMBINED_DESKTOP_ARTIFACT_DIRECTORY")!;
        var original = PersonalCloudArtifactDirectory.Load(source); var packaged = PersonalCloudArtifactDirectory.Load(copied);
        Assert.Equal("local-combined-parity", packaged.Deployment.Version); Assert.Equal(original.Deployment.Version, packaged.Deployment.Version);
        Assert.Equal(24, packaged.Migrations.Migrations.Count);
        Assert.Equal("0024_source_identity_lookup.sql", packaged.Migrations.Migrations[^1].Name);
        Assert.Equal("832c48167bdf82c70bbdf4330d89a21c5e4c72f037e96efe9353fece2aa6e7ce", packaged.Migrations.Migrations[^1].Sha256);
        Assert.Equal(SHA256.HashData(original.Deployment.WorkerModule), SHA256.HashData(packaged.Deployment.WorkerModule));
        Assert.Contains("provider.scope.put", Encoding.UTF8.GetString(packaged.Deployment.WorkerModule));
        Assert.Contains("workspacePaging", Encoding.UTF8.GetString(packaged.Deployment.WorkerModule));
        Assert.Contains(packaged.Deployment.Assets, a => a.Path == "/workspace-controls.js");
        Assert.Contains(packaged.Deployment.Assets, a => a.Path == "/initial-sync-status.js");
    }
    private sealed class CombinedArtifactAcceptanceAttribute : FactAttribute
    {
        public CombinedArtifactAcceptanceAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENZA_COMBINED_ARTIFACT_DIRECTORY")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENZA_COMBINED_DESKTOP_ARTIFACT_DIRECTORY")))
                Skip = "Opt-in combined local source artifact and Desktop copy verification.";
        }
    }
}
