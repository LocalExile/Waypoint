using Launcher.Core;

namespace Waypoint.Tests;

public sealed class FoldersTests
{
    static string Temp() => Path.Combine(Path.GetTempPath(), "waypoint-folders-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_move_copies_everything_and_removes_the_old_folder_only_after()
    {
        string root = Temp(), from = Path.Combine(root, "old"), to = Path.Combine(root, "new");
        try
        {
            Directory.CreateDirectory(Path.Combine(from, "backups", "abc"));
            Directory.CreateDirectory(Path.Combine(from, "empty"));
            File.WriteAllText(Path.Combine(from, "backups", "abc", "saves.db"), "saves");
            File.WriteAllText(Path.Combine(from, "settings.json"), "{}");

            Assert.Empty(Folders.Move(from, to));
            Assert.False(Directory.Exists(from));
            Assert.Equal("saves", File.ReadAllText(Path.Combine(to, "backups", "abc", "saves.db")));
            Assert.True(Directory.Exists(Path.Combine(to, "empty")));

            // Into a folder with files, or one inside the other: refused, nothing touched.
            Directory.CreateDirectory(from);
            File.WriteAllText(Path.Combine(from, "x"), "x");
            Assert.Throws<IOException>(() => Folders.Move(from, to));
            Assert.Throws<IOException>(() => Folders.Move(from, Path.Combine(from, "inside")));
            Assert.True(File.Exists(Path.Combine(from, "x")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_move_that_fails_leaves_the_old_folder_as_it_was_and_the_new_one_empty()
    {
        string root = Temp(), from = Path.Combine(root, "old"), to = Path.Combine(root, "new");
        try
        {
            Directory.CreateDirectory(Path.Combine(from, "a"));
            File.WriteAllText(Path.Combine(from, "a", "first"), "1");
            File.WriteAllText(Path.Combine(from, "locked"), "2");
            // A file another program holds cannot be read: the copy fails part way.
            using (new FileStream(Path.Combine(from, "locked"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.Throws<IOException>(() => Folders.Move(from, to));

            Assert.Equal("1", File.ReadAllText(Path.Combine(from, "a", "first")));
            Assert.Equal("2", File.ReadAllText(Path.Combine(from, "locked")));
            Assert.True(Folders.Empty(to));
            Assert.Null(Folders.Unusable(to));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_file_in_place_of_a_folder_reports_a_translatable_reason()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "keep");
            Assert.Equal(L.Text("The path points to a file."), Folders.Unusable(file));
            Assert.Equal("keep", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Chosen_tools_go_first_on_PATH_each_once_and_the_rest_keeps_its_order()
    {
        char s = Path.PathSeparator;
        Assert.Equal($"C:\\Git\\cmd{s}C:\\a{s}C:\\b", ToolPaths.Front($"C:\\a{s}C:\\Git\\cmd{s}C:\\b", ["C:\\Git\\cmd", "c:\\git\\CMD"]));
        Assert.Equal($"C:\\a{s}C:\\b", ToolPaths.Front($"C:\\a{s}C:\\b", []));
    }
}
