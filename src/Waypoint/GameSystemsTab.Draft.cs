using System.Text.Json.Nodes;
#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using A11y = System.Windows.Automation.AutomationProperties;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using A11y = Avalonia.Automation.AutomationProperties;
#endif

namespace Waypoint;

public sealed partial class GameSystemsTab
{
    TextBox? _draftName, _draftDescription;
    TextBlock? _nameFeedback;
    GameLeague? _draftTemplate;
    GameLeague[] _draftCandidates = [];
    int _draftStep;

    void StartCreate(GameLeague? origin = null)
    {
        if (_catalog is null || Busy || !Discard()) return;
        if (!_catalog.Environment.CanCreate) { SetStatus(L.Text("This checkout does not expose supported league creation. Existing leagues remain available for inspection.")); return; }
        _draftCandidates = _catalog.Leagues.Where(l => !l.Custom && _catalog.Configurable(l) && _catalog.FlagOf(l) is not null && GameSystems.EmptyTemplate(l.Folder)).ToArray();
        if (_draftCandidates.Length == 0) { SetStatus(L.Text("This league has custom code or no reusable source template. It cannot be safely copied.")); return; }
        if (origin is not null && !_draftCandidates.Contains(origin)) { SetStatus(L.Text("This league has custom code or no reusable source template. It cannot be safely copied.")); return; }
        _draftTemplate = origin ?? _draftCandidates[0];
        _draftName = new TextBox();
        _draftDescription = new TextBox { Text = _draftTemplate.Description, AcceptsReturn = true, MinHeight = 72 };
        A11y.SetIsRequiredForForm(_draftName, true);
        A11y.SetHelpText(_draftName, L.Text("Use 3–48 English letters, numbers, spaces, underscores or hyphens, starting with a letter."));
        _draftName.TextChanged += (_, _) => { SetStatus(""); UpdateFeedback(); };
        _draftDescription.TextChanged += (_, _) => { SetStatus(""); UpdateFeedback(); };
        _draftModules.Clear(); foreach (var path in _draftTemplate.Systems) _draftModules.Add(path);
        _choicesDirty = false; _creating = true; _draftStep = 1; RenderDraft();
    }
    void RenderDraft()
    {
        ClearView(); _creating = true;
        if (_draftTemplate is null || _catalog is null || _draftName is null || _draftDescription is null) return;
        if (_draftStep == 1) RenderDraftBasics(); else RenderDraftModules();
        UpdateFeedback(); UpdateLock();
    }
    void RenderDraftBasics()
    {
        var title = Type(L.Text("1 of 2 · League and mode"), 20, true); title.Margin = new Thickness(0, 0, 0, 8); _detail.Children.Add(title);
        _detail.Children.Add(SettingsEditor.Note(L.Text("Start with an existing league, choose its mode, then give your custom league a name.")));
        var families = _catalog!.Cards.Select(c => new GameLeagueCard(c.Name, c.Variants.Where(_draftCandidates.Contains).ToArray()))
            .Where(c => c.Variants.Length > 0).ToArray();
        var currentFamily = families.First(f => f.Variants.Contains(_draftTemplate!));
        var familySelector = new ComboBox { ItemsSource = families, SelectedItem = currentFamily, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
        _detail.Children.Add(LabelFor(familySelector, L.Text("Starting flag"))); _detail.Children.Add(familySelector);
        var modes = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
        _detail.Children.Add(LabelFor(modes, L.Text("Game mode"))); _detail.Children.Add(modes);
        var modeHint = SettingsEditor.Note(""); _detail.Children.Add(modeHint);
        bool choosing = false;
        void FillModes(GameLeagueCard family, GameLeague? selected)
        {
            choosing = true; modes.Items.Clear();
            foreach (var profile in family.Variants) modes.Items.Add(ModeItem(profile, family.Variants));
            modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, selected))
                ?? modes.Items.OfType<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, family.Primary));
            modeHint.Text = family.Variants.Length == 1 ? L.Text("Only {0} is defined for this league. Other modes cannot be selected.", _catalog!.Modes(family.Primary))
                : L.Text("Only modes defined for this league are offered.");
            choosing = false;
        }
        FillModes(currentFamily, _draftTemplate);
        bool Switch(GameLeague next)
        {
            if (_choicesDirty && !Dialog.Ask(Owner, DialogKind.Question, L.Text("Unsaved changes"), L.Text("Discard the module choices in this draft and use the selected profile?"), L.Text("Discard"), L.Text("Cancel"))) return false;
            string oldDescription = _draftTemplate!.Description; _draftTemplate = next;
            if (_draftDescription!.Text == oldDescription) _draftDescription.Text = next.Description;
            _draftModules.Clear(); foreach (var path in next.Systems) _draftModules.Add(path);
            _choicesDirty = false; SetStatus(""); UpdateFeedback(); return true;
        }
        familySelector.SelectionChanged += (_, _) =>
        {
            if (choosing || familySelector.SelectedItem is not GameLeagueCard next) return;
            if (!Switch(next.Primary)) { choosing = true; familySelector.SelectedItem = currentFamily; choosing = false; return; }
            currentFamily = next; FillModes(next, next.Primary);
        };
        modes.SelectionChanged += (_, _) =>
        {
            if (choosing || (modes.SelectedItem as ComboBoxItem)?.Tag is not GameLeague next || ReferenceEquals(next, _draftTemplate)) return;
            if (!Switch(next)) { choosing = true; modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, _draftTemplate)); choosing = false; }
        };
        _detail.Children.Add(LabelFor(_draftName!, L.Text("New league name"))); _detail.Children.Add(_draftName!);
        _nameFeedback = SettingsEditor.Note(""); _detail.Children.Add(_nameFeedback);
        _detail.Children.Add(LabelFor(_draftDescription!, L.Text("Description shown in game"))); _detail.Children.Add(_draftDescription!);
        _primary = FooterButton(L.Text("Next: modules"), () => { _draftStep = 2; RenderDraft(); }, primary: true);
        FooterButton(L.Text("Cancel"), DiscardCurrent);
        Dispatch(() => _draftName!.Focus());
    }
    void RenderDraftModules()
    {
        _nameFeedback = null;
        var title = Type(L.Text("2 of 2 · Modules and review"), 20, true); title.Margin = new Thickness(0, 0, 0, 6); _detail.Children.Add(title);
        _detail.Children.Add(SettingsEditor.Label(_draftName!.Text + " · " + _catalog!.FlagOf(_draftTemplate!)!.Name + " · " + _catalog.Modes(_draftTemplate!)));
        _detail.Children.Add(SettingsEditor.Note(L.Text("Creates one separate flag named {0}. Its chosen mode is fixed; no mode checkboxes are added.", _draftName.Text ?? "")));
        _detail.Children.Add(SettingsEditor.Note(_draftTemplate!.Hardcore ? L.Text("This league stays separate from Standard. Hardcore death is permanent.")
            : L.Text("This league has its own characters and stash. Nothing will migrate into Standard.")));
        var search = AddModuleChoices(_detail, _draftTemplate, editable: true);
        var review = new StackPanel { Margin = new Thickness(12, 8, 0, 8) };
        review.Children.Add(SettingsEditor.Label(L.Text("New league: {0}", _draftName.Text ?? "")));
        review.Children.Add(SettingsEditor.Note(_draftDescription!.Text ?? ""));
        review.Children.Add(SettingsEditor.Note(L.Text("Existing leagues, characters and stashes will stay as they are.")));
        review.Children.Add(SettingsEditor.Note(L.Text("Parent league: {0}", "Void")));
        review.Children.Add(SettingsEditor.Note(L.Text("Saved choices apply after the next server restart. Existing characters remain in their league.")));
        _detail.Children.Add(new Expander { Header = L.Text("Review details"), Content = review, IsExpanded = false, Margin = new Thickness(0, 10, 0, 8) });
        FooterButton(L.Text("Back to name and mode"), () => { _draftStep = 1; RenderDraft(); });
        _primary = FooterButton(L.Text("Create league"), () => _ = CreateLeagueAsync(), primary: true);
        FooterButton(L.Text("Cancel"), DiscardCurrent);
        Dispatch(() => search.Focus());
    }
    /// <summary>What the draft leaves out of the bundle of the league it starts from; empty when that bundle is whole.</summary>
    string DraftBundleWarning() => _catalog is not null && _draftTemplate is not null && _catalog.BundleLeftOut(_draftTemplate, _draftModules) is { Count: > 0 } left
        ? L.Text("Without {0}, the new league has the mode of {1} but not its rules or content.", string.Join(", ", left.Select(ModuleTitle)), _draftTemplate.Name) : "";

    string? DraftError()
    {
        if (_draftTemplate is null || _catalog is null) return L.Text("Choose a starting league." );
        try { _catalog.PlanCreate(_draftTemplate, _draftName?.Text ?? "", _draftDescription?.Text ?? "", _draftModules); return null; }
        catch (InvalidOperationException e) { return e.Message; }
    }
    void UpdateDraftFeedback()
    {
        string? error = DraftError();
        if (_draftStep == 1)
        {
            string? nameError = _catalog?.NewNameError(_draftName?.Text ?? "");
            if (_nameFeedback is not null) _nameFeedback.Text = string.IsNullOrEmpty(_draftName?.Text) ? L.Text("Give your league a name to continue.")
                : nameError ?? L.Text("This name is unused by your current leagues.");
            _validation.Text = error is null ? L.Text("Next, choose modules and review your league.") : string.IsNullOrEmpty(_draftName?.Text) || nameError is not null
                ? L.Text("Complete the league details to continue.") : L.Text("Cannot continue: {0}", error);
        }
        // Nothing waits for the servers to stop: LocalExile reads its leagues when a server starts, so a league made
        // while one runs is there at its next start.
        else _validation.Text = error is not null ? L.Text("Cannot create: {0}", error)
            : L.Text("Ready to create. Existing leagues and saves are preserved.") + (DraftBundleWarning() is { Length: > 0 } apart ? " " + apart : "");
        if (_primary is not null) _primary.IsEnabled = !Busy && error is null;
    }
    async Task CreateLeagueAsync()
    {
        if (_catalog is null || _draftTemplate is null || Busy) return;
        try
        {
            string name = _draftName?.Text ?? "";
            var request = _catalog.PlanCreate(_draftTemplate, name, _draftDescription?.Text ?? "", _draftModules);
            string consequence = _draftTemplate.Hardcore ? L.Text("Hardcore death in this custom league is permanent.") : L.Text("Its characters and stash stay separate from Standard.");
            // The new league takes its mode from the league it starts from, whose own bundle may have been unticked.
            string apart = DraftBundleWarning();
            if (!Dialog.Ask(Owner, apart.Length > 0 ? DialogKind.Warning : DialogKind.Question, L.Text("Create league"),
                    L.Text("Create the league \"{0}\" in {1} mode? {2} Existing leagues and saves are preserved.", name, _catalog.Modes(_draftTemplate), consequence)
                    + (apart.Length > 0 ? "\n\n" + apart : ""), L.Text("Create"), L.Text("Cancel"))) return;
            var result = await SaveAsync("CreateLeague", request);
            if (result?.Ok != true) return;
            _creating = false; _choicesDirty = false; await RefreshAsync();
            _list.SelectedItem = _list.Items.OfType<GameLeagueCard>().FirstOrDefault(f => f.Variants.Any(l => l.Name == name));
            SetStatus(L.Text("League created. It becomes available after the next server start."));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { SetStatus(e.Message); }
    }
}
