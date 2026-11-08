using Waypoint;

namespace Waypoint.Tests;

// Repo's release selection is process-wide, so this check must not overlap other test collections.
[CollectionDefinition("Mod release presentation", DisableParallelization = true)]
public class ModReleasePresentationCollection { }

[Collection("Mod release presentation")]
public class ModsReleasePresentationTests
{
    [Fact]
    public void Release_rows_allow_saved_choices_but_never_offer_a_package_write()
    {
        bool before = Repo.IsInstall;
        try
        {
            Repo.IsInstall = true;
            var entry = new ModEntry("example", "1.0.0", "feature", [], true, "Not checked", "", "", "", "binary-hash");
            var listing = new ModListing("example", "Example", "", "", "1.1.0", "feature", true, "mods/example", [], true);
            var offer = new BrowseItem(ModSource.Official, listing, null, entry);
            Assert.False(offer.CanAct);
            var row = new InstalledRow(entry, offer);
            Assert.True(row.CanToggle);
            Assert.False(row.CanAct);
            Assert.False(new InstalledRow(entry with { Fingerprint = "" }, offer).CanToggle);
            var release = new ModRelease("example", ReleaseVersion.Parse("1.1.0")!, "example-v1.1.0", "", "", "", 0, null, null);
            Assert.False(new VersionRow(offer, release).CanAct);
        }
        finally { Repo.IsInstall = before; }
    }
}
