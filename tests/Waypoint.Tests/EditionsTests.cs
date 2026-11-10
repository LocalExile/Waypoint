using Waypoint;

namespace Waypoint.Tests;

// Two tests set Repo.IsInstall, which every test shares: they run apart from the others, as the release's mod tests do.
[Collection("Release mod compatibility")]
public sealed class EditionsTests
{
    [Fact]
    public void Someone_on_Latest_in_Simple_mode_is_guided_until_they_answer_and_never_forced()
    {
        bool install = Repo.IsInstall;
        try
        {
            Repo.IsInstall = false;
            Assert.True(Editions.Guides(new Settings()));
            // "Stay on Latest" ends the card and keeps the switch: the way to the release stays.
            Assert.False(Editions.Guides(new Settings { LatestGuideDismissed = true }));
            Assert.True(Editions.Offered(new Settings { LatestGuideDismissed = true }));
            // Developer Mode has made its choice, and a release install has nothing to be guided away from.
            Assert.False(Editions.Guides(new Settings { DeveloperMode = true }));
            Repo.IsInstall = true;
            Assert.False(Editions.Guides(new Settings()));
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
    public void Simple_mode_offers_the_Latest_side_only_to_those_who_asked_for_it()
    {
        string folder = Path.Combine(Path.GetTempPath(), "waypoint-editions-" + Guid.NewGuid().ToString("N"));
        string latest = Path.Combine(folder, "latest");
        bool install = Repo.IsInstall;
        try
        {
            Directory.CreateDirectory(Path.Combine(latest, "scripts", "dev"));
            File.WriteAllText(Path.Combine(latest, "GameServer.slnx"), "");
            File.WriteAllText(Path.Combine(latest, "scripts", "dev", "live.ps1"), "");

            // A release install: someone who came to play sees neither the switch nor the copy from source.
            Repo.IsInstall = true;
            Assert.False(Editions.Offered(new Settings()));
            Assert.True(Editions.Offered(new Settings { DeveloperMode = true }));
            // A Latest copy set up before stays reachable, and one that is gone is not offered.
            Assert.True(Editions.Offered(new Settings { LatestFolder = latest }));
            Assert.False(Editions.Offered(new Settings { LatestFolder = Path.Combine(folder, "gone") }));

            // On a source copy the switch stays: it is the way back to the release.
            Repo.IsInstall = false;
            Assert.True(Editions.Offered(new Settings()));
        }
        finally
        {
            Repo.IsInstall = install;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
