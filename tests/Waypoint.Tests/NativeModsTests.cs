using Launcher.Core.NativeMods;
using LocalExile.Client.Waypoint;
using Waypoint;

namespace Waypoint.Tests;

public class NativeModsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waypoint-nativemods-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NativeMods_opt_in_defaults_to_off_and_proxy_mode()
    {
        var settings = new Settings();
        var pref = settings.NativeModsFor(_root);
        Assert.False(pref.Enabled);
        Assert.Equal(nameof(ExtenderInstallMode.ProxyVersionDll), pref.Mode);

        settings.SetNativeMods(new NativeModsPreference(true, nameof(ExtenderInstallMode.ImportTable)), _root);
        string json = System.Text.Json.JsonSerializer.Serialize(settings);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<Settings>(json)!;
        var roundTrip = loaded.NativeModsFor(_root);
        Assert.True(roundTrip.Enabled);
        Assert.Equal(nameof(ExtenderInstallMode.ImportTable), roundTrip.Mode);
    }

    [Fact]
    public void InstalledRow_and_BrowseItem_surface_Requires_NativeMods_badge_and_warning_when_off()
    {
        var summary = new NativeClientSummary(Required: true, RawAccess: true, RawAccessJustification: "Custom hook", DatPatchCount: 2);
        var entry = new ModEntry("garden", "1.0.0", "feature", [], true, "Gateway stopped", "", "", "", "hash", "Harvest Garden")
        {
            NativeClient = summary,
            ClientPayloadInstalled = true,
        };
        var listing = new ModListing("garden", "Harvest Garden", "Desc", "Author", "1.0.0", "feature", false, "mods/garden", [], false)
        {
            NativeClient = summary,
        };

        var rowOff = new InstalledRow(entry, null) { NativeModsDisabled = true };
        Assert.True(rowOff.HasNativeClient);
        Assert.True(rowOff.RequiresNativeMods);
        Assert.True(rowOff.ShowNativeModsWarning);
        Assert.Equal(L.Text("Requires NativeMods"), rowOff.NativeModsBadgeText);
        Assert.Contains(L.Text("Requires NativeMods"), rowOff.Metadata);
        Assert.NotNull(rowOff.NativeModsWarningTip);

        var rowOn = new InstalledRow(entry, null) { NativeModsDisabled = false };
        Assert.False(rowOn.ShowNativeModsWarning);
        Assert.Null(rowOn.NativeModsWarningTip);

        var item = new BrowseItem(ModSource.Official, listing, null, null) { NativeModsDisabled = true };
        Assert.True(item.ShowNativeModsWarning);
        Assert.Contains(L.Text("Requires NativeMods"), item.Metadata);

        var tags = ModsText.Tags(item, entry);
        Assert.Contains(L.Text("Requires NativeMods"), tags);
        Assert.Contains(L.Text("Raw access"), tags);
        Assert.Contains(L.Text("Dat patches"), tags);

        Assert.NotEmpty(ModsText.NativeModsNotice([entry], nativeModsEnabled: false));
        Assert.Empty(ModsText.NativeModsNotice([entry], nativeModsEnabled: true));
        Assert.Equal("tables could not be built", ModsText.NativeModsNotice([entry], nativeModsEnabled: true, launchWarning: "tables could not be built"));
    }

    [Fact]
    public void Launch_environment_builder_handles_Windows_and_Proton_opt_in_and_opt_out()
    {
        var linuxOn = NativeModsExtender.BuildLaunchEnvironment("/opt/LocalExile", optedIn: true, ExtenderInstallMode.ProxyVersionDll, isLinux: true, "dxgi=n");
        Assert.Equal(@"Z:\opt\LocalExile\Client\nativemods-host", linuxOn["NATIVEMODS_HOST_DIR"]);
        Assert.Equal(@"Z:\opt\LocalExile\Client\nativemods-host\addresses", linuxOn["NATIVEMODS_ADDRESSES_DIR"]);
        Assert.Equal(@"Z:\opt\LocalExile\Mods\Client", linuxOn["NATIVEMODS_MODS_DIR"]);
        Assert.Equal(@"Z:\opt\LocalExile\logs\nativemods-shim.log", linuxOn["NATIVEMODS_LOG_PATH"]);
        Assert.Equal("dxgi=n;version=n,b", linuxOn["WINEDLLOVERRIDES"]);

        var linuxOff = NativeModsExtender.BuildLaunchEnvironment("/opt/LocalExile", optedIn: false, ExtenderInstallMode.ProxyVersionDll, isLinux: true, "dxgi=n;version=n,b");
        Assert.Null(linuxOff["NATIVEMODS_HOST_DIR"]);
        Assert.Null(linuxOff["NATIVEMODS_MODS_DIR"]);
        Assert.Equal("dxgi=n", linuxOff["WINEDLLOVERRIDES"]);

        var merged = NativeModsLaunch.MergeEnvironment(Scripts.ClientOnlyEnvironment, linuxOn)!;
        Assert.Equal("http://127.0.0.1:1", merged["HTTP_PROXY"]);
        Assert.Equal("dxgi=n;version=n,b", merged["WINEDLLOVERRIDES"]);

        Assert.Equal(@"Z:\opt\LocalExile\Mods\Client", NativeModsExtender.ToWinePath("/opt/other/../LocalExile/./Mods/Client"));
        Assert.Equal(@"Z:\", NativeModsExtender.ToWinePath("/"));
    }

    [Fact]
    public async Task Preflight_fails_when_opted_in_without_installed_extender_and_succeeds_when_opted_out()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Client"));
        var settings = new Settings();

        var offPlan = await NativeModsLaunch.PrepareAsync(_root, settings, isReleaseInstall: false);
        Assert.True(offPlan.Ok);
        Assert.Null(offPlan.Environment["NATIVEMODS_HOST_DIR"]);

        settings.SetNativeMods(new NativeModsPreference(true, nameof(ExtenderInstallMode.ProxyVersionDll)), _root);
        var onPlan = await NativeModsLaunch.PrepareAsync(_root, settings, isReleaseInstall: false);
        Assert.False(onPlan.Ok);
        Assert.NotNull(onPlan.Error);
    }

    [Fact]
    public async Task Opted_out_launch_leaves_a_version_dll_NativeMods_did_not_install()
    {
        string client = Path.Combine(_root, "Client");
        Directory.CreateDirectory(client);
        string dll = Path.Combine(client, "version.dll");
        File.WriteAllText(dll, "another tool");

        var plan = await NativeModsLaunch.PrepareAsync(_root, new Settings(), isReleaseInstall: false);

        Assert.True(plan.Ok);
        Assert.Equal("another tool", File.ReadAllText(dll));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
