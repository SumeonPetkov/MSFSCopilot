using SkyLink.Sim;

namespace SkyLink.Integration.Tests;

public sealed class BridgeInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skylink-installer-" + Guid.NewGuid().ToString("N"));

    public BridgeInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Package(string version, bool built = true, string creator = "SkyLink")
    {
        string dir = Path.Combine(_root, "src-" + version + "-" + creator);
        Directory.CreateDirectory(Path.Combine(dir, "modules"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            $$"""{ "title": "SkyLink Bridge", "creator": "{{creator}}", "package_version": "{{version}}", "content_type": "MISC" }""");
        if (built)
        {
            File.WriteAllBytes(Path.Combine(dir, "modules", "SkyLinkBridge.wasm"), [0x00, 0x61, 0x73, 0x6D]);
            File.WriteAllText(Path.Combine(dir, "layout.json"), """{ "content": [] }""");
        }
        return dir;
    }

    private string Community()
    {
        string dir = Path.Combine(_root, "Community");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [InlineData("InstalledPackagesPath \"C:\\Users\\X\\AppData\\Roaming\\Microsoft Flight Simulator 2024\\Packages\"", @"C:\Users\X\AppData\Roaming\Microsoft Flight Simulator 2024\Packages")]
    [InlineData("{Graphics\n}\n  InstalledPackagesPath \"D:\\MSFS Packages\"\n", @"D:\MSFS Packages")]
    [InlineData("SomethingElse \"x\"", null)]
    public void Reads_the_packages_path_from_UserCfg(string cfg, string? expected) =>
        Assert.Equal(expected, BridgeInstaller.ParseInstalledPackagesPath(cfg));

    [Fact]
    public void Refuses_a_package_whose_module_is_not_built()
    {
        var e = Assert.Throws<InvalidOperationException>(() => BridgeInstaller.Install(Package("0.3.0", built: false), Community()));
        Assert.Contains("not built yet", e.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "Community", "skylink-bridge")));
    }

    [Fact]
    public void Never_touches_a_folder_it_did_not_create()
    {
        string community = Community();
        string foreign = Path.Combine(community, "skylink-bridge");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "manifest.json"), """{ "creator": "Someone Else", "package_version": "9" }""");

        Assert.Throws<InvalidOperationException>(() => BridgeInstaller.Install(Package("0.3.0"), community));
        Assert.Equal("Someone Else", BridgeInstaller.ReadPackage(foreign)!.Creator);
    }

    [Fact]
    public void Refuses_a_source_that_is_not_ours() =>
        Assert.Contains("not a SkyLink package", Assert.Throws<InvalidOperationException>(
            () => BridgeInstaller.Install(Package("1.0", creator: "FS Copilot"), Community())).Message);

    [Fact]
    public void Installs_then_updates_and_dry_run_changes_nothing()
    {
        string community = Community();
        string target = Path.Combine(community, "skylink-bridge");

        Assert.Contains("would have installed", BridgeInstaller.Install(Package("0.3.0"), community, dryRun: true));
        Assert.False(Directory.Exists(target));

        Assert.Contains("installed 0.3.0", BridgeInstaller.Install(Package("0.3.0"), community));
        Assert.True(File.Exists(Path.Combine(target, "modules", "SkyLinkBridge.wasm")));

        Assert.Contains("updated 0.3.0 -> 0.4.0", BridgeInstaller.Install(Package("0.4.0"), community));
        Assert.Equal("0.4.0", BridgeInstaller.ReadPackage(target)!.Version);
        Assert.False(Directory.Exists(target + ".skylink-new"));
    }

    [Fact]
    public void Shipped_manifest_is_a_valid_SkyLink_package()
    {
        string? manifestDir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && manifestDir is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "package", "skylink-bridge", "manifest.json")))
                manifestDir = Path.Combine(d.FullName, "package", "skylink-bridge");
        Assert.NotNull(manifestDir);
        var info = BridgeInstaller.ReadPackage(manifestDir!);
        Assert.NotNull(info);
        Assert.Equal("SkyLink", info!.Creator);
        Assert.False(string.IsNullOrEmpty(info.Version));
    }
}
