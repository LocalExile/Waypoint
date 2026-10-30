using GameServer.Core.Accounts;
using GameServer.Storage;

namespace GameServer.Launcher;

sealed record AccountRow(string Name, string Created, int Characters, IReadOnlyList<CharacterRow> CharacterRows);

sealed record CharacterRow(string Name, string Class, int Level, string League, string LastTown, string LastPlayed, string State);

/// <summary>The accounts and characters in the save database, read only (writes go through the dev host). No database
/// yet means no accounts: the launcher does not create one.</summary>
static class AccountList
{
    public static List<AccountRow> Load()
    {
        if (!File.Exists(SaveDatabase.DefaultPath)) return [];
        using var db = new SaveDatabase(SaveDatabase.DefaultPath);
        var store = new SqliteAccountStore(db);
        return [.. store.Accounts().Select(a =>
        {
            var characters = store.CharactersOf(a.Id).Select(Row).ToList();
            return new AccountRow(a.Name, a.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), characters.Count, characters);
        })];
    }

    static CharacterRow Row(Character c) => new(c.Name, c.Class.ToString(), c.Level, c.League, c.LastTown ?? "(never in town)",
        c.LastPlayedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        c.HardcoreDeath is { PermanentlyDead: true } ? "dead (permanent)"
            : c.HardcoreDeath is { } death ? $"died in {death.SourceLeague}" : "");
}
