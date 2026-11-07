using Waypoint;

namespace Waypoint.Tests;

[CollectionDefinition("Release mod compatibility", DisableParallelization = true)]
public class ReleaseModCompatibilityCollection;

[Collection("Release mod compatibility")]
public class ReleaseModCompatibilityTests
{
    [Fact]
    public void Legacy_release_cannot_install_new_mods_or_offer_updates()
    {
        bool before = Repo.IsInstall;
        try
        {
            Repo.IsInstall = true;
            Assert.Throws<InvalidOperationException>(ModBrowser.RequireModernMods);
            var listing = new ModListing("example", "Example", "", "", "2.0.0", "feature", true, "mods/example", [], false);
            var offer = new BrowseItem(ModSource.Official, listing, null, null);
            Assert.False(offer.CanAct);
            var release = new ModRelease("example", ReleaseVersion.Parse("2.0.0")!, "example-v2.0.0", "", "example.zip", "", 0, null, null);
            Assert.False(new VersionRow(offer, release).CanAct);
            Repo.IsInstall = false;
            ModBrowser.RequireModernMods(); Assert.True(offer.CanAct);
        }
        finally { Repo.IsInstall = before; }
    }
}
