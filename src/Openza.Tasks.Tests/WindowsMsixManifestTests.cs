using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Openza.Tasks.Tests;

public sealed class WindowsMsixManifestTests
{
    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));

    [Theory]
    [InlineData("Dev", "Package.Dev.appxmanifest", "0.1.3.0")]
    [InlineData("Production", "Package.appxmanifest", "1.4.0.0")]
    public void Manifest_preserves_WinUI_identity_and_increases_version(string channel, string baselineFile, string version)
    {
        var root = RepositoryRoot();
        var baseline = XDocument.Load(Path.Combine(root, "src/Openza.Tasks/" + baselineFile));
        var desktop = XDocument.Load(Path.Combine(root, $"packaging/windows/Package.{channel}.appxmanifest"));
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var oldIdentity = baseline.Root!.Element(ns + "Identity")!;
        var identity = desktop.Root!.Element(ns + "Identity")!;
        foreach (var name in new[] { "Name", "Publisher", "ProcessorArchitecture" })
            Assert.Equal((string?)oldIdentity.Attribute(name), (string?)identity.Attribute(name));
        Assert.Equal(version, (string?)identity.Attribute("Version"));
        Assert.True(Version.Parse((string)identity.Attribute("Version")!) >
            Version.Parse((string)oldIdentity.Attribute("Version")!));
        Assert.Equal(baseline.Root.Element(ns + "Properties")!.ToString(),
            desktop.Root.Element(ns + "Properties")!.ToString());
        Assert.Equal(baseline.Root.Element(ns + "Dependencies")!.ToString(),
            desktop.Root.Element(ns + "Dependencies")!.ToString());
        if (channel == "Production")
        {
            XNamespace phone = "http://schemas.microsoft.com/appx/2014/phone/manifest";
            Assert.Equal(baseline.Root.Element(phone + "PhoneIdentity")!.ToString(),
                desktop.Root.Element(phone + "PhoneIdentity")!.ToString());
        }
        var app = desktop.Descendants(ns + "Application").Single();
        Assert.Equal((string?)baseline.Descendants(ns + "Application").Single().Attribute("Id"), (string?)app.Attribute("Id"));
        Assert.Equal("Windows.FullTrustApplication", (string?)app.Attribute("EntryPoint"));
        Assert.Equal("Openza.Tasks.Desktop.exe", (string?)app.Attribute("Executable"));
        XNamespace restricted = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
        Assert.Contains(desktop.Descendants(restricted + "Capability"), c => (string?)c.Attribute("Name") == "runFullTrust");
        foreach (var attribute in desktop.Descendants().Attributes().Where(a => a.Value.StartsWith("Assets\\", StringComparison.Ordinal)))
            Assert.True(File.Exists(Path.Combine(root, "src/Openza.Tasks", attribute.Value.Replace('\\', Path.DirectorySeparatorChar))));
    }
}
