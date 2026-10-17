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
        Assert.Equal(original.Deployment.Version, packaged.Deployment.Version);
        Assert.Equal(25, packaged.Migrations.Migrations.Count);
        Assert.Equal("0025_task_updated_at.sql", packaged.Migrations.Migrations[^1].Name);
        Assert.Equal(original.Migrations.Migrations[^1].Sha256, packaged.Migrations.Migrations[^1].Sha256);
        Assert.All(packaged.Migrations.Migrations, migration => Assert.DoesNotContain("\r", migration.Sql));
        // This migration was deployed from Linux before Windows parity work.
        Assert.Equal("c85b27348316ffb02161d510561f411728441df6efb4c02c65629603f24ea954", packaged.Migrations.Migrations[0].Sha256);
        Assert.Equal(SHA256.HashData(original.Deployment.WorkerModule), SHA256.HashData(packaged.Deployment.WorkerModule));
        Assert.Contains("provider.scope.put", Encoding.UTF8.GetString(packaged.Deployment.WorkerModule));
        Assert.Contains("workspacePaging", Encoding.UTF8.GetString(packaged.Deployment.WorkerModule));
        Assert.Contains(packaged.Deployment.Assets, a => a.Path == "/workspace-controls.js");
        Assert.Contains(packaged.Deployment.Assets, a => a.Path == "/initial-sync-status.js");
        Assert.Contains(packaged.Deployment.Assets, a => a.Path == "/task-row-actions.js");
        Assert.Contains(packaged.Deployment.Assets, a => a.Path == "/fonts/InterVariable.woff2");
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
