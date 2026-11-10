using Waypoint;

namespace Waypoint.Tests;

// Two tests set Repo.IsInstall, which every test shares: they run apart from the others, as the release's mod tests do.
[Collection("Release mod compatibility")]
public sealed class EditionsTests
{
    [Fact]
    public void Simple_mode_plays_the_release_and_a_source_copy_asks_for_the_release_or_Advanced_mode()
    {
        bool install = Repo.IsInstall;
        try
        {
            // A source copy in Simple mode: no Play, the choice instead. Developer Mode alone is still Simple mode.
            Repo.IsInstall = false;
            Assert.True(Editions.MustChoose(new Settings()));
            Assert.True(Editions.MustChoose(new Settings { DeveloperMode = true, AdvancedMode = false }));
            // Advanced mode is one of the two answers: the copy plays there.
            Assert.False(Editions.MustChoose(new Settings { DeveloperMode = true, AdvancedMode = true }));
            // The release is the other: Simple mode plays it.
            Repo.IsInstall = true;
            Assert.False(Editions.MustChoose(new Settings()));
            Assert.False(Editions.MustChoose(new Settings { DeveloperMode = true, AdvancedMode = true }));
        }
        finally { Repo.IsInstall = install; }
    }

    [Fact]
    public void A_remembered_folder_counts_only_while_it_is_still_that_kind()
    {
        string folder = Path.Combine(Path.GetTempPath(), "waypoint-editions-" + Guid.NewGuid().ToString("N"));
        string release = Path.Combine(folder, "release"), latest = Path.Combine(folder, "latest");
        try
        {
            Directory.CreateDirectory(release);
            File.WriteAllText(Path.Combine(release, "LocalExile.exe"), "");
            File.WriteAllText(Path.Combine(release, "release.json"), """{"version":"0.1.0","commit":"cc0f9c3","client":"3.29.3b"}""");
            Directory.CreateDirectory(Path.Combine(latest, "scripts", "dev"));
            File.WriteAllText(Path.Combine(latest, "GameServer.slnx"), "");
            File.WriteAllText(Path.Combine(latest, "scripts", "dev", "live.ps1"), "");
            var settings = new Settings { ReleaseFolder = release, LatestFolder = latest };

            Assert.Equal(release, Editions.Folder(settings, release: true));
            Assert.Equal(latest, Editions.Folder(settings, release: false));

            // Swapped, or gone: neither side is offered as the other.
            Assert.Null(Editions.Folder(new Settings { ReleaseFolder = latest, LatestFolder = release }, release: true));
            Assert.Null(Editions.Folder(new Settings { ReleaseFolder = latest, LatestFolder = release }, release: false));
            Directory.Delete(release, recursive: true);
            Assert.Null(Editions.Folder(settings, release: true));
            Assert.Null(Editions.Folder(new Settings(), release: false));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_release_shows_the_way_to_Latest_only_with_Developer_Mode()
    {
        bool install = Repo.IsInstall;
        try
        {
            // A release install: someone who came to play sees neither the switch nor the copy from source,
            // whether or not a Latest copy was set up before.
            Repo.IsInstall = true;
            Assert.False(Editions.Offered(new Settings()));
            Assert.False(Editions.Offered(new Settings { LatestFolder = Path.GetTempPath() }));
            Assert.True(Editions.Offered(new Settings { DeveloperMode = true }));

            // On a source copy the page has the choice instead of the switch.
            Repo.IsInstall = false;
            Assert.False(Editions.Offered(new Settings()));
            Assert.False(Editions.Offered(new Settings { DeveloperMode = true }));
        }
        finally { Repo.IsInstall = install; }
    }
}
