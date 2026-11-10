using Waypoint;

namespace Waypoint.Tests;

[CollectionDefinition("Release mod compatibility", DisableParallelization = true)]
public class ReleaseModCompatibilityCollection;

[Collection("Release mod compatibility")]
public class ReleaseModCompatibilityTests
{
    static readonly ModListing Listing = new("example", "Example", "", "", "2.0.0", "feature", true, "mods/example", [], false, ["0.2.0-beta"]);

    [Fact]
    public void Legacy_release_cannot_install_new_mods_or_offer_updates()
    {
        var (install, client) = (Repo.IsInstall, Repo.ClientVersion);
        try
        {
            Repo.IsInstall = true;
            Repo.ClientVersion = ReleaseVersion.Parse("0.1.0-beta");
            Assert.Throws<InvalidOperationException>(ModBrowser.RequireModernMods);
            var offer = new BrowseItem(ModSource.Official, Listing, null, null);
            Assert.False(offer.CanAct);
            var release = new ModRelease("example", ReleaseVersion.Parse("2.0.0")!, "example-v2.0.0", "", "example.zip", "", 0, null, null);
            Assert.False(new VersionRow(offer, release).CanAct);
            Repo.ClientVersion = null;
            Assert.False(offer.CanAct);
            Repo.IsInstall = false;
            ModBrowser.RequireModernMods(); Assert.True(offer.CanAct);
        }
        finally { (Repo.IsInstall, Repo.ClientVersion) = (install, client); }
    }

    [Fact]
    public void A_release_made_for_downloadable_mods_installs_them()
    {
        var (install, client) = (Repo.IsInstall, Repo.ClientVersion);
        try
        {
            Repo.IsInstall = true;
            Repo.ClientVersion = ReleaseVersion.Parse("0.2.0-beta");
            ModBrowser.RequireModernMods();
            var offer = new BrowseItem(ModSource.Official, Listing, null, null);
            Assert.True(offer.CanAct);
            Assert.False(offer.Incompatible);
        }
        finally { (Repo.IsInstall, Repo.ClientVersion) = (install, client); }
    }

    [Theory]
    [InlineData("0.1.0-beta", false)] // Legacy: refused with its own message.
    [InlineData("0.2.0-beta", true)]
    [InlineData("0.2.1-beta", true)]
    [InlineData("0.2.2-beta", false)]
    [InlineData("0.3.0", false)]
    public void A_release_that_cannot_load_compiled_mods_is_warned_and_never_refused(string version, bool warned)
    {
        var (install, client) = (Repo.IsInstall, Repo.ClientVersion);
        try
        {
            Repo.IsInstall = true;
            Repo.ClientVersion = ReleaseVersion.Parse(version);
            Assert.Equal(warned, Repo.ReleaseModsMayNotLoad);
            Assert.Equal(warned, ModsText.MayNotLoadNotice().Length > 0);
            if (warned)
            {
                ModBrowser.RequireModernMods();
                Assert.True(new BrowseItem(ModSource.Official, Listing, null, null).CanAct);
                Assert.Contains("v0.2.2-beta", ModsText.MayNotLoadNotice());
            }
            // A source checkout builds its own GameServer.Modding: never warned.
            Repo.IsInstall = false;
            Assert.False(Repo.ReleaseModsMayNotLoad);
        }
        finally { (Repo.IsInstall, Repo.ClientVersion) = (install, client); }
    }

    [Fact]
    public void An_incompatible_mod_is_marked_but_still_installable()
    {
        var (install, client) = (Repo.IsInstall, Repo.ClientVersion);
        try
        {
            Repo.IsInstall = false;
            Repo.ClientVersion = ReleaseVersion.Parse("0.3.0");
            var offer = new BrowseItem(ModSource.Official, Listing, null, null);
            Assert.True(offer.Incompatible);
            Assert.NotNull(offer.IncompatibleTip);
            Assert.True(offer.CanAct);
            Assert.Equal("0.2.0-beta", offer.ClientText);
            // A mod that names no versions, or a checkout of unknown version, is not judged.
            Assert.False((offer with { Listing = Listing with { ClientVersions = null } }).Incompatible);
            Repo.ClientVersion = null;
            Assert.False(offer.Incompatible);
        }
        finally { (Repo.IsInstall, Repo.ClientVersion) = (install, client); }
    }
}
