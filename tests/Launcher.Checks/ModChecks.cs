using System.Text.Json;
using Launcher.Core;

static class ModChecks
{
    public static void Run(string fixture, Action<bool, string> check)
    {
        string root = Path.Combine(fixture, "mod catalog");
        string folder = Path.Combine(root, "Mods", "Binaries");
        var stopped = new RuntimeSnapshot(root, "test", "", "", "", "", [],
            [new("Gateway", "Stopped", 0, "", "", [], 0, 0, "")], [], ModEnvironment: new(true, folder, true));
        check(ModCatalog.Read(stopped with { ModEnvironment = null }).Length == 0, "older snapshots without mod support remain readable");
        void Binary(string id, string manifest)
        {
            string path = Path.Combine(folder, id);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "mod.json"), manifest);
            File.WriteAllText(Path.Combine(path, id + ".dll"), "inert inventory fixture; never loaded");
        }
        Binary("alpha", "{ // JSONC metadata\n\"id\":\"alpha\",\"version\":\"preview build\",\"enabled\":false, }");
        string source = Path.Combine(root, "Mods", "Source", "Alpha");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Alpha.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(source, "mod.json"), "{\"id\":\"alpha\"}");
        File.WriteAllText(Path.Combine(folder, "mod-state.txt"), "alpha on\n");
        var alpha = ModCatalog.Read(stopped).Single();
        check(alpha.Version == "preview build" && alpha.Enabled && alpha.Project.EndsWith("Alpha.csproj") && alpha.Fingerprint.Length == 64,
            "JSONC metadata, free-text versions, source mapping, saved enable overrides and binary fingerprints are read without assembly loading");
        check(alpha.Status == "Gateway stopped", "a disk DLL is not presented as a loaded runtime mod");
        File.AppendAllText(Path.Combine(folder,"alpha","alpha.dll"), "changed");
        check(ModCatalog.Read(stopped).Single().Fingerprint != alpha.Fingerprint, "changed binaries invalidate cached inventory fingerprints");
        Binary("beta", "{\"depends\":[\"missing\"]}");
        check(ModCatalog.Read(stopped).Single(m => m.Id == "beta").Status == "Dependency blocked", "missing dependencies are visible before launch");
        Binary("escape", "{\"assembly\":\"../other.dll\"}");
        check(ModCatalog.Read(stopped).Single(m => m.Id == "escape").Status == "Invalid manifest", "manifest DLL paths cannot escape the selected mod folder");
        Binary("wrong", "{\"id\":\"other\"}");
        check(ModCatalog.Read(stopped).Single(m => m.Id == "wrong").Status == "Invalid manifest", "binary manifest IDs must match their actual folder");
        check(!ModCatalog.ValidId("../outside") && !ModCatalog.ValidId("hello\n/mod_disable alpha") && ModCatalog.ValidId("example.mod-1"), "only bounded single mod IDs can reach command arguments");
        var parsed = ModCatalog.ParseRuntimeReport("--- Mods in Mods/Binaries/ ---\nalpha preview build - feature, 3 hook(s)\nbeta - not loaded, needs missing (no such mod)\ngamma - not loaded, disabled\nchat: alpha 1 - feature, 99 hook(s)");
        check(parsed.Count == 3 && parsed["alpha"].Status == "Loaded (last checked)" && parsed["beta"].Status == "Load failed" && parsed["gamma"].Status == "Disabled",
            "runtime lists distinguish loaded, disabled and failed mods and reject unrelated lines");
        var confirmed = stopped with { ModEnvironment = new(true, folder, true, "alpha preview build - feature, 3 hook(s)", "2026-10-30T00:00:00Z") };
        check(ModCatalog.Read(confirmed).Single(m => m.Id == "alpha").Status == "Loaded (last checked)", "explicit runtime reports remain labelled as last checked");
        check(ModCatalog.Read(stopped with { ModEnvironment = new(true, folder, false) }).Single(m => m.Id == "alpha").Status == "Mods disabled globally", "global mods-off is separate from an individual saved enable choice");

        string database = Path.Combine(root, "data", "saves.db");
        string backup = Path.Combine(BackupCatalog.DirectoryFor(database), "20261030-000000-1234abcd");
        string store = BackupCatalog.DirectoryFor(database);
        if (Directory.Exists(store) || !ModCatalog.Physical(store)) throw new IOException("Expected a new physical store for the GUID-based temporary database path.");
        Directory.CreateDirectory(backup);
        try
        {
            File.WriteAllText(Path.Combine(backup, "saves.db"), "inert backup inventory fixture");
            File.WriteAllText(Path.Combine(backup, "backup.json"), JsonSerializer.Serialize(new
            {
                SaveDatabase = database, CreatedUtc = "2026-10-30T00:00:00Z", Mods = new { Choices = "alpha off", Inventory = new[] { new { Id = "alpha", Manifest = "{\"version\":\"2\"}", Assemblies = new Dictionary<string,string> { ["alpha.dll"] = new string('a',64) } } } }
            }));
            var save = BackupCatalog.Read(database).Single();
            check(save.HasModChoices && save.ModSummary == "alpha 2", "backup history exposes recorded mod versions and separate mod-choice restore capability");
            check(save.ModHistory.Contains("alpha: disabled") && save.ModHistory.Contains(new string('a',64)), "backup details preserve saved enable choices and complete DLL fingerprints");
            File.WriteAllText(Path.Combine(backup, "backup.json"), JsonSerializer.Serialize(new
            {
                SaveDatabase = database, CreatedUtc = "2026-10-30T00:00:00Z", Mods = new { Choices = "", Inventory = new[] { new { Id = "alpha", Manifest = "[]" } } }
            }));
            check(BackupCatalog.Read(database).Single().ModHistory.Contains("enable choice unknown"), "a broken mod manifest does not hide an otherwise readable save backup");
        }
        finally
        {
            File.Delete(Path.Combine(backup, "backup.json"));
            File.Delete(Path.Combine(backup, "saves.db"));
            Directory.Delete(backup); Directory.Delete(store);
        }
    }
}
