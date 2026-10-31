using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record PlayerCharacter(string Id, string Name, int Level, string Class, string Ascendancy,
    string League, string LastPlayedUtc, bool HasProgress)
{
    public string DisplayClass => string.IsNullOrWhiteSpace(Ascendancy) ? L.Text(Class) : Ascendancy;
    public string ClassDetail => string.IsNullOrWhiteSpace(Ascendancy) ? "" : L.Text(Class);
    public string Played => DateTimeOffset.TryParse(LastPlayedUtc, out var date) ? date.ToLocalTime().ToString("g", L.Current.Culture) : L.Text("unknown");
    public override string ToString() => L.Text("{0}, level {1}, {2}, {3}", Name, Level, DisplayClass, League);
}
public sealed record PlayerAccount(string Id, string Name, string CreatedUtc, string Revision, PlayerCharacter[] Characters, int StashTabs)
{
    public string Summary => L.Text(Characters.Length == 1 ? "{0} character" : "{0} characters", Characters.Length) + " · " +
        L.Text(StashTabs == 1 ? "{0} stash tab" : "{0} stash tabs", StashTabs);
    public override string ToString() => Name;
}
public sealed record AccountInventory(bool Exists, bool Writable, string Notice, PlayerAccount[] Accounts, bool CanRegister = false);
public sealed record AccountDataRequest(string Action, string AccountId, string ExpectedRevision, string Name = "",
    string Password = "", bool IncludeCharacters = true, bool IncludeStash = true, Dictionary<string, string>? CharacterNames = null);

public static class AccountTools
{
    public static bool ValidName(string name, int maximum = 32) => Regex.IsMatch(name, @"\A[A-Za-z0-9_-]{3," + maximum + @"}\z");
    public static Dictionary<string, string> ProposeCharacterNames(PlayerAccount source, string newAccount, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>();
        foreach (var character in source.Characters)
        {
            string stem = Regex.Replace(character.Name + "_" + newAccount, "[^A-Za-z0-9_-]", "");
            if (stem.Length < 3) stem = "Character";
            stem = stem[..Math.Min(23, stem.Length)];
            string candidate = stem;
            for (int suffix = 2; !used.Add(candidate); suffix++)
            {
                string tail = "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                candidate = stem[..Math.Min(stem.Length, 23 - tail.Length)] + tail;
            }
            result[character.Id] = candidate;
        }
        return result;
    }
}
