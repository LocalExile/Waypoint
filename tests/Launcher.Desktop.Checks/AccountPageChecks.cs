using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Launcher.Core;
using Launcher.Desktop;

static class AccountPageChecks
{
    public static void Run(App app, MainWindow window, Action<bool, string> check)
    {
        var appearance = app.Appearance; string language = L.Current.Language;
        var view = (AccountManagerView)window.FindName("AccountsView");
        var ch = new PlayerCharacter("character", "EmberWitch", 84, "Witch", "Elementalist", "Standard", "2026-01-01T00:00:00Z", true);
        var account = new PlayerAccount("source", "Aurora", "", "revision", [ch], 1);
        var inventory = new AccountInventory(true, true, "", [account, account with { Id = "other", Name = "AtlasTester", Characters = [] }], true);
        var stopped = new RuntimeSnapshot("fixture", "", "fixture.db", "", "", "", [], [], []);
        try
        {
            L.Current.SetLanguage("en"); app.ApplyAppearance(new());
            view.SetInventory(inventory); view.SetRuntime(stopped, false, true);
            check(view.Selected?.Id == account.Id && view.ChangesEnabled, "Offline supported accounts enable reviewed mutations independently of game-client preparation.");
            view.SearchBox.Text = "elementalist";
            check(view.Selected?.Id == account.Id, "Account search finds a character's ascendancy.");
            view.SearchBox.Text = "no-match";
            check(view.Selected is null && !view.ChangesEnabled, "An empty filtered list cannot retain destructive actions for a hidden account.");
            view.SearchBox.Clear();
            view.SetInventory(inventory with { Accounts = [account with { Name = "Renamed" }] });
            check(view.Selected is { Id: "source", Name: "Renamed" }, "Account selection follows identity after rename and refresh.");
            foreach (string service in new[] { "Game", "Auth", "Gateway" })
            {
                view.SetRuntime(stopped with { Services = [new(service, "Running", 42, "", "", [], 0, 0, "")] }, false, true);
                check(!view.ChangesEnabled && view.Selected is not null, service + " makes account data read-only without hiding characters.");
            }
            view.SetRuntime(stopped with { AccountWriteBlockedReason = "Account service is running" }, false, true);
            check(!view.ChangesEnabled, "An account-service blocker disables writes even without a standard listener/service row.");
            view.SetRuntime(stopped, false, true); view.SetLoading(true);
            check(!view.ChangesEnabled, "Loading disables edits until a complete account snapshot is available.");
            view.SetLoading(false); view.SetRuntime(stopped, true, true);
            check(!view.ChangesEnabled, "An active launcher operation disables account writes.");
            view.SetRuntime(stopped, false, true); view.SetInventory(inventory with { Writable = false, CanRegister = false });
            check(!view.ChangesEnabled, "Unsupported schemas remain browsable without enabling edits.");
            foreach (string action in new[] { "Create", "Duplicate", "Rename", "Password", "Delete" })
            {
                var dialog = new AccountActionDialog(window, action, action == "Create" ? null : account, inventory.Accounts, true, "reviewed-fixture.db");
                dialog.ApplyTemplate();
                var root = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
                root.Measure(new Size(420, 540)); root.Arrange(new Rect(0, 0, 420, 540)); root.UpdateLayout();
                check(!Descendants<TextBlock>(root).Any(t => t.Text.StartsWith("Text_", StringComparison.Ordinal)), action + " has translated presentation resources instead of raw resource keys.");
                check(Descendants<TextBlock>(root).Any(t => t.Text == "reviewed-fixture.db"), action + " identifies the save database being reviewed before confirmation.");
                if (action == "Delete")
                {
                    check(Descendants<Button>(root).Any(b => b.IsCancel && b.IsDefault), "Account deletion keeps Cancel as the safe Enter default.");
                    typeof(AccountActionDialog).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, null);
                    check(dialog.Request is null, "Deletion cannot produce a request until the exact account name is entered.");
                }
                if (action == "Duplicate")
                {
                    var mode = (ListBox)typeof(AccountActionDialog).GetField("mode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                    check(mode.SelectedIndex == 0 && mode.Items.Count == 2, "Account duplication defaults to full copy and offers an empty account.");
                }
                dialog.Close(); check(dialog.Request is null, "Closing an account dialog never submits its action.");
            }
            L.Current.SetLanguage("ru"); app.ApplyAppearance(new(Mode: "Light", Font: "OpenDyslexic", TextScale: 150));
            var large = new AccountManagerView(); large.ApplyTemplate();
            large.Measure(new Size(668, 600)); large.Arrange(new Rect(0, 0, 668, 600)); large.UpdateLayout();
            var heading = Descendants<TextBlock>(large).Single(t => t.Text == L.Text("Accounts"));
            check(heading.ActualWidth >= 150 && heading.ActualHeight < 100,
                "Russian 150% reading text keeps the Accounts heading intact above wrapping action buttons at minimum workspace width.");
        }
        finally { view.SetInventory(null); view.SetRuntime(null, false, true); L.Current.SetLanguage(language); app.ApplyAppearance(appearance); }
    }
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
