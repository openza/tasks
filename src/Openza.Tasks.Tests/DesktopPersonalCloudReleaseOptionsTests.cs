using System.Reflection;
using System.Reflection.Emit;
using Openza.Tasks.Core.PersonalCloud;
using Openza.Tasks.Desktop.Services;

namespace Openza.Tasks.Tests;

public class DesktopPersonalCloudReleaseOptionsTests
{
    [Fact]
    public void FeedDefaultsToProductionWithoutSwitch()
    {
        var options = Options("Dev", _ => null);
        Assert.Equal(new Uri("https://api.github.com/"), options.ApiRoot);
        Assert.Equal([PersonalCloudReleaseKeys.Production], options.PublicKeysPem);
    }

    [Theory]
    [InlineData("Dev")]
    [InlineData("Preview")]
    [InlineData("Production")]
    public void LocalFeedSwitchIsRestrictedToDebugDev(string channel)
    {
        var keyFile = Path.GetTempFileName();
        try
        {
            const string testPublicKey = "synthetic public key for options selection only";
            File.WriteAllText(keyFile, testPublicKey);
            var reads = 0;
            var options = Options(channel, name =>
            {
                reads++;
                return name.EndsWith("API_ROOT", StringComparison.Ordinal) ? "http://127.0.0.1:18788/" : keyFile;
            });
#if DEBUG
            if (channel == "Dev")
            {
                Assert.Equal(new Uri("http://127.0.0.1:18788/"), options.ApiRoot);
                Assert.Equal([testPublicKey], options.PublicKeysPem);
                return;
            }
#endif
            Assert.Equal(0, reads);
            Assert.Equal(new Uri("https://api.github.com/"), options.ApiRoot);
            Assert.Equal([PersonalCloudReleaseKeys.Production], options.PublicKeysPem);
        }
        finally { File.Delete(keyFile); }
    }

    private static PersonalCloudReleaseFeedOptions Options(string channel, Func<string, string?> environment)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("SyntheticCloudHost"), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!, ["OpenzaChannel", channel]));
        return (PersonalCloudReleaseFeedOptions)typeof(DesktopPersonalCloudSetup)
            .GetMethod("ReleaseFeedOptions", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [assembly, environment])!;
    }
}
