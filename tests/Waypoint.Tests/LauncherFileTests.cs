namespace Waypoint.Tests;

/// <summary>Which file a self-update replaces (<see cref="LauncherFile"/>): the published exe on Windows, and on Linux the
/// AppImage the runtime names, only while Waypoint runs from inside that AppImage's own image.</summary>
public class LauncherFileTests
{
    static bool Exists(string path) => path == "/home/player/Apps/Waypoint-x86_64.AppImage" || path == "/home/player/Apps/My Waypoint.AppImage";

    [Fact]
    public void EachSystemHasItsReleaseFile()
    {
        Assert.Equal("Waypoint.exe", LauncherFile.ExeName);
        Assert.Equal("Waypoint-x86_64.AppImage", LauncherFile.AppImageName);
    }

    [Theory]
    [InlineData("C:/Tools/Waypoint.exe", "C:/Tools/Waypoint.exe")]
    [InlineData("C:/Tools/waypoint.EXE", "C:/Tools/waypoint.EXE")]
    [InlineData("C:/Program Files/dotnet/dotnet.exe", null)]
    [InlineData(null, null)]
    public void OnWindowsOnlyThePublishedExeUpdates(string? process, string? expected) =>
        Assert.Equal(expected, LauncherFile.Running(linux: false, process, "/ignored.AppImage", "/ignored", _ => true));

    [Fact]
    public void OnLinuxTheAppImageItRunsFromIsReplaced()
    {
        Assert.Equal("/home/player/Apps/Waypoint-x86_64.AppImage",
            LauncherFile.Running(linux: true, "/tmp/.mount_WaypoiAbc/usr/bin/Waypoint", "/home/player/Apps/Waypoint-x86_64.AppImage", "/tmp/.mount_WaypoiAbc", Exists));
        // Its user may have renamed it: it keeps that name.
        Assert.Equal("/home/player/Apps/My Waypoint.AppImage",
            LauncherFile.Running(linux: true, "/tmp/.mount_WaypoiAbc/usr/bin/Waypoint", "/home/player/Apps/My Waypoint.AppImage", "/tmp/.mount_WaypoiAbc/", Exists));
    }

    [Theory]
    // dotnet run, or a build from source: no AppImage at all.
    [InlineData("/home/player/Waypoint/bin/Waypoint", null, null)]
    // Started from inside another AppImage, which left its variables: this process is not in that image.
    [InlineData("/home/player/Waypoint/bin/Waypoint", "/home/player/Apps/Waypoint-x86_64.AppImage", "/tmp/.mount_Other")]
    // A mount whose name only begins like this one's.
    [InlineData("/tmp/.mount_WaypoiAbcdef/usr/bin/Waypoint", "/home/player/Apps/Waypoint-x86_64.AppImage", "/tmp/.mount_WaypoiAbc")]
    // Relative paths are not the runtime's.
    [InlineData("/tmp/.mount_WaypoiAbc/usr/bin/Waypoint", "Waypoint-x86_64.AppImage", "/tmp/.mount_WaypoiAbc")]
    [InlineData("/tmp/.mount_WaypoiAbc/usr/bin/Waypoint", "/home/player/Apps/Waypoint-x86_64.AppImage", "tmp/.mount_WaypoiAbc")]
    // The AppImage is not there any more.
    [InlineData("/tmp/.mount_WaypoiAbc/usr/bin/Waypoint", "/home/player/Apps/Gone.AppImage", "/tmp/.mount_WaypoiAbc")]
    // A file inside the image is never one to replace.
    [InlineData("/tmp/.mount_WaypoiAbc/usr/bin/Waypoint", "/tmp/.mount_WaypoiAbc/usr/bin/Waypoint", "/tmp/.mount_WaypoiAbc")]
    public void OnLinuxNothingElseIsTakenForTheAppImage(string process, string? appImage, string? appDir) =>
        Assert.Null(LauncherFile.Running(linux: true, process, appImage, appDir, path => Exists(path) || path.StartsWith("/tmp/", StringComparison.Ordinal)));
}
