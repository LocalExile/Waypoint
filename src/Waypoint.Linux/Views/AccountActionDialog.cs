using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace Waypoint;

sealed class AccountActionDialog : FormWindow
{
    readonly string action;
    readonly PlayerAccount? source;
    readonly TextBox name = new() { MaxLength = 32, Text = "" };
    readonly TextBox password = new() { MaxLength = 1024, PasswordChar = '•', Text = "" }, confirmation = new() { MaxLength = 1024, PasswordChar = '•', Text = "" };
    readonly ListBox mode = new() { ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()) };
    readonly StackPanel copiedNames = new();
    readonly Dictionary<string, TextBox> characterNames = [];
    readonly string[] occupied;
    readonly TextBlock error = new();
    public AccountDataRequest? Request { get; private set; }

    public AccountActionDialog(Window? owner, string action, PlayerAccount? source, IReadOnlyList<PlayerAccount> accounts, bool automaticBackup, string database = "")
    {
        this.action = action; this.source = source;
        occupied = accounts.SelectMany(a => a.Characters).Select(c => c.Name).ToArray();
        string title = action switch { "Create" => "Create account", "Duplicate" => "Duplicate account", "Rename" => "Rename account", "Password" => "Set account password", "Delete" => "Delete account", _ => throw new ArgumentException("Unknown account action") };
        DialogLayout.Configure(this, owner, L.Text(title), 760);
        MaxHeight = DialogLayout.WorkHeight(this, owner) - 40; Height = Math.Min(760, MaxHeight); MinHeight = 380; SizeToContent = SizeToContent.Manual;
        var root = new DockPanel { Margin = new Thickness(24) };
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom);
        error.Foreground = App.Brush("Shell.Error"); AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite); footer.Children.Add(error);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), ItemSpacing = 8 };
        var apply = AccountUi.Button(title, Apply, action != "Delete"); apply.IsDefault = action != "Delete";
        if (action == "Delete") apply.Foreground = App.Brush("Shell.Error");
        var cancel = Dismiss(L.Text("Cancel")); cancel.IsDefault = action == "Delete";
        buttons.Children.Add(apply); buttons.Children.Add(cancel); footer.Children.Add(buttons); root.Children.Add(footer);
        var body = new StackPanel();
        var heading = AccountUi.Text(title, 22); heading.FontWeight = FontWeight.SemiBold; body.Children.Add(heading);
        if (source is not null)
        {
            body.Children.Add(new TextBlock { Text = source.Name, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            body.Children.Add(new TextBlock { Text = source.Summary, Margin = new Thickness(0, 0, 0, 14) });
        }
        if (database.Length > 0)
        {
            var locationLabel = AccountUi.Text("Save database", 12, true); locationLabel.Margin = new Thickness(0, 8, 0, 4); body.Children.Add(locationLabel);
            var location = new TextBlock { Text = database, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
            ToolTip.SetTip(location, database); body.Children.Add(location);
        }
        var backup = AccountUi.Text(automaticBackup ? action == "Create" ? "An existing save database will be backed up before account creation." : "A save backup will be created before this change." : "Automatic backups are OFF. This change will not create a recovery backup.", 12, automaticBackup);
        if (!automaticBackup) backup.Foreground = App.Brush("Shell.Warning"); body.Children.Add(backup);
        if (action == "Delete")
        {
            var warning = AccountUi.Text("This removes this account, all of its characters and progress, and its personal and guild stashes in every league. Other accounts are unchanged.");
            warning.Margin = new Thickness(0, 16, 0, 8); body.Children.Add(warning);
            Field(body, "Type the exact account name to delete it", name);
        }
        else if (action != "Password")
        {
            Field(body, action == "Rename" ? "New account name" : "Account name", name);
            body.Children.Add(AccountUi.Text("3–32 letters, digits, underscores or hyphens.", 12, true));
            name.Text = action == "Rename" ? source!.Name : action == "Duplicate" ? SuggestAccountName(source!.Name, accounts) : "";
        }
        if (action is "Create" or "Duplicate" or "Password")
        {
            Field(body, "New password", password); Field(body, "Confirm new password", confirmation);
            body.Children.Add(AccountUi.Text("At least 4 characters. Credentials are not saved by Waypoint.", 12, true));
            if (action == "Password") body.Children.Add(AccountUi.Text("Saved client credentials for this account will need to be entered again.", 12, true));
        }
        if (action == "Duplicate")
        {
            mode.ItemsSource = new[] { L.Text("Full copy"), L.Text("Start empty") }; mode.SelectedIndex = 0;
            Field(body, "What to copy", mode);
            body.Children.Add(AccountUi.Text("Full copy includes characters, progress and all personal/guild stash tabs. Start empty creates only a new login.", 12, true));
            var namesHeader = AccountUi.Text("Character names for the copy", 16); namesHeader.Margin = new Thickness(0, 16, 0, 8); copiedNames.Children.Add(namesHeader);
            body.Children.Add(copiedNames); RegenerateNames();
            name.TextChanged += (_, _) => RegenerateNames(); mode.SelectionChanged += (_, _) => copiedNames.IsVisible = mode.SelectedIndex == 0;
        }
        var stopped = AccountUi.Text("Keep the game and all LocalExile services stopped until the change finishes.", 12, true); stopped.Margin = new Thickness(0, 16, 0, 0); body.Children.Add(stopped);
        root.Children.Add(new ScrollViewer { Content = body }); Content = root;
        Opened += (_, _) => { if (action == "Delete") cancel.Focus(); else if (action == "Password") password.Focus(); else { name.Focus(); name.SelectAll(); } };
        Closed += (_, _) => { password.Clear(); confirmation.Clear(); };
    }

    static void Field(Panel body, string label, Control control)
    {
        var caption = AccountUi.Text(label); caption.Margin = new Thickness(0, 14, 0, 5); body.Children.Add(caption);
        AutomationProperties.SetName(control, L.Text(label)); body.Children.Add(control);
    }

    static string SuggestAccountName(string source, IReadOnlyList<PlayerAccount> accounts)
    {
        string stem = System.Text.RegularExpressions.Regex.Replace(source, "[^A-Za-z0-9_-]", "");
        if (stem.Length < 3) stem = "Account";
        for (int i = 1; ; i++)
        {
            string suffix = i == 1 ? "_copy" : "_copy" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string candidate = stem[..Math.Min(stem.Length, 32 - suffix.Length)] + suffix;
            if (!accounts.Any(a => string.Equals(a.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    void RegenerateNames()
    {
        while (copiedNames.Children.Count > 1) copiedNames.Children.RemoveAt(1);
        characterNames.Clear();
        foreach (var pair in AccountTools.ProposeCharacterNames(source!, name.Text ?? "", occupied))
        {
            var label = new TextBlock { Text = source!.Characters.Single(c => c.Id == pair.Key).Name, Margin = new Thickness(0, 8, 0, 4) }; copiedNames.Children.Add(label);
            var input = new TextBox { Text = pair.Value, MaxLength = 23 }; AutomationProperties.SetName(input, L.Text("New name for {0}", label.Text));
            characterNames[pair.Key] = input; copiedNames.Children.Add(input);
        }
        if (source!.Characters.Length == 0) copiedNames.Children.Add(AccountUi.Text("This account has no characters to copy.", 12, true));
    }

    void Apply()
    {
        void Fail(string text, Control field) { error.Text = L.Text(text); field.Focus(); }
        string typed = name.Text ?? "", secret = password.Text ?? "";
        if (action == "Delete")
        {
            if (typed != source!.Name) { Fail("The account name must match exactly. Nothing has been deleted.", name); return; }
        }
        else if (action != "Password" && !AccountTools.ValidName(typed)) { Fail("Enter a valid account name: 3–32 letters, digits, underscores or hyphens.", name); return; }
        if (action is "Create" or "Duplicate" or "Password")
        {
            if (secret.Length < 4) { Fail("The password must have at least 4 characters.", password); return; }
            if (secret != (confirmation.Text ?? "")) { Fail("The passwords do not match.", confirmation); return; }
        }
        bool full = action == "Duplicate" && mode.SelectedIndex == 0;
        var names = full ? characterNames.ToDictionary(p => p.Key, p => p.Value.Text ?? "") : null;
        if (names is not null && (names.Values.Any(n => !AccountTools.ValidName(n, 23)) || names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count ||
            names.Values.Any(n => occupied.Contains(n, StringComparer.OrdinalIgnoreCase))))
        { error.Text = L.Text("Copied characters need unique names with 3–23 letters, digits, underscores or hyphens."); return; }
        Request = new(action, source?.Id ?? "", source?.Revision ?? "", typed, secret, full, full, names);
        Accept();
    }
}
