namespace Waypoint.Tests;

/// <summary>Steam for Linux keeps Path of Exile where a walk of the home folder never reaches it: the finder reads Steam's
/// roots and the libraries each one's <c>libraryfolders.vdf</c> names (<see cref="ClientLocator.SteamClients"/>).</summary>
public class SteamLibraryTests : IDisposable
{
    readonly string _home = Path.Combine(Path.GetTempPath(), "Waypoint-steam-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    const string Libraries = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"/home/player/.local/share/Steam"
        		"label"		""
        		"apps"
        		{
        			"238960"		"74731092708"
        		}
        	}
        	"1"
        	{
        		"path"		"/mnt/games/SteamLibrary"
        		"apps"
        		{
        		}
        	}
        	"2"
        	{
        		"path"		"D:\\Games\\Steam \"Library\""
        	}
        }
        """;

    [Fact]
    public void EveryLibraryPathIsReadInItsOrderWithSteamsEscapingUndone()
    {
        Assert.Equal(["/home/player/.local/share/Steam", "/mnt/games/SteamLibrary", @"D:\Games\Steam ""Library"""], ClientLocator.SteamLibraryPaths(Libraries));
    }

    [Fact]
    public void AFileWithoutLibrariesNamesNone()
    {
        Assert.Empty(ClientLocator.SteamLibraryPaths(""));
        Assert.Empty(ClientLocator.SteamLibraryPaths("\"libraryfolders\" { \"0\" { \"label\" \"path\" } }"));
    }

    [Fact]
    public void TheGamesFolderComesFromItsManifestAndNeverLeavesTheLibrary()
    {
        Assert.Equal("Path of Exile", ClientLocator.SteamInstallDir("\"AppState\"\n{\n\t\"appid\"\t\t\"238960\"\n\t\"installdir\"\t\t\"Path of Exile\"\n}"));
        Assert.Null(ClientLocator.SteamInstallDir("\"installdir\" \"..\""));
        Assert.Null(ClientLocator.SteamInstallDir("\"installdir\" \"../elsewhere\""));
        Assert.Null(ClientLocator.SteamInstallDir("\"name\" \"Path of Exile\""));
    }

    [Fact]
    public void EachSteamRootAndTheLibrariesItNamesAreLooked()
    {
        string native = Path.Combine(_home, ".local", "share", "Steam"), flatpak = Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        string other = Path.Combine(_home, "drive", "SteamLibrary");
        Directory.CreateDirectory(Path.Combine(native, "steamapps"));
        Directory.CreateDirectory(Path.Combine(flatpak, "steamapps"));
        Directory.CreateDirectory(Path.Combine(other, "steamapps"));
        File.WriteAllText(Path.Combine(native, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{native.Replace(@"\", @"\\")}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{other.Replace(@"\", @"\\")}\"\n\t}}\n}}");
        File.WriteAllText(Path.Combine(other, "steamapps", "appmanifest_238960.acf"), "\"AppState\"\n{\n\t\"installdir\"\t\t\"PoE\"\n}");

        var found = ClientLocator.SteamClients(_home).ToList();

        Assert.Equal(
        [
            Path.Combine(native, "steamapps", "common", "Path of Exile"),
            Path.Combine(other, "steamapps", "common", "PoE"),
            Path.Combine(flatpak, "steamapps", "common", "Path of Exile"),
        ], found);
    }

    [Fact]
    public void AHomeWithoutSteamGivesNothing()
    {
        Directory.CreateDirectory(_home);
        Assert.Empty(ClientLocator.SteamClients(_home));
    }

    [Fact]
    public void TheLinkSteamMakesInItsDotFolderIsARoot()
    {
        Assert.Contains(Path.Combine(_home, ".steam", "steam"), ClientLocator.SteamRoots(_home));
        Assert.Contains(Path.Combine(_home, ".steam", "root"), ClientLocator.SteamRoots(_home));
    }
}
