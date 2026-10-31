using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using Launcher.Core;

namespace Launcher.Desktop;

/// <summary>Live resource binding, including tooltips and automation names.</summary>
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = L.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

public partial class MainWindow
{
    bool changingLanguage;
    void ApplyLanguage()
    {
        L.Current.SetLanguage(settings.Language);
        Language = XmlLanguage.GetLanguage(L.Current.Culture.Name);
        // Recreate presentation rows only; repository selection and runtime operations are unchanged.
        RefreshNavigationLabels();
        AccountsPage.Header = L.Text("Accounts");
        AccountsView.RefreshLanguage();
        UpdateAppearanceSummary();
        UpdateClosePreferences();
        LoadDevelopmentPreferences();
        UpdateWindowFrame();
        BrandSubtitle.Text = L.Text(brandSubtitle);
        bool multiple = Application.Current is App { AllowMultipleInstances: true };
        MultipleInstanceLabel.Text = L.Text("Multiple-instance test mode");
        MultipleInstanceLabel.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;
        Title = BrandName.Text + (multiple ? " — " + L.Text("Multiple-instance test mode") : "");
        VersionLabel.Text = L.Text("Version ") + brandVersion;
        headingRoot = null;
        UpdateWorkspaceHeading(settings.RepositoryPath);
        if (snapshot is not null) Render(snapshot);
        else { ApplyModFilter(); UpdateSourceSelection(); UpdateBackupSelection(); }
        RefreshBackups();
        ApplySourceFilter();
        foreach (var row in serviceRows) row.RefreshLanguage();
        ModsList.Items.Refresh();
        DiscoveryStatus.Text = L.Text(discovering
            ? "Checking upstream HEAD and searching common local folders… You can still Browse to choose a location."
            : string.IsNullOrWhiteSpace(settings.RepositoryPath)
                ? "Automatic lookup runs on first use. You can also search again here."
                : "Using your selected checkout. Change location or search again at any time.");
        changingLanguage = true;
        LanguageBox.SelectedIndex = L.Current.Language == "ru" ? 1 : 0;
        changingLanguage = false;
    }
    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || changingLanguage || LanguageBox.SelectedItem is not ListBoxItem item) return;
        string selected = (string)item.Tag;
        if (selected == L.Current.Language) return;
        try
        {
            var next = settings with { Language = selected };
            SettingsStore.Save(next);
            settings = next;
            ApplyLanguage();
            StatusLabel.Text = L.Text("Language saved.");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            changingLanguage = true;
            LanguageBox.SelectedIndex = L.Current.Language == "ru" ? 1 : 0;
            changingLanguage = false;
            StatusLabel.Text = L.Text("Could not save preferences: ") + ex.Message;
            SetFeedback(true);
        }
    }
}
