using Launcher.Core.NativeMods;
using LocalExile.Client.Waypoint;

namespace Waypoint;

/// <summary>Result of pre-launch NativeMods validation and environment preparation.</summary>
sealed record NativeModsLaunchPlan(bool Ok, IReadOnlyDictionary<string, string?> Environment, string? Error = null);

/// <summary>
/// Coordinates NativeMods pre-launch checks and environment variable injection for both source checkouts
/// (<c>live.ps1</c>) and release installs (<c>LocalExile.exe --json</c>), on both Windows and Linux/Proton.
/// </summary>
static class NativeModsLaunch
{
    public static Task<NativeModsLaunchPlan> PrepareAsync(
        string root,
        Settings settings,
        bool isReleaseInstall,
        Action<string>? log = null)
    {
        var pref = settings.NativeModsFor(root);
        var mode = NativeModsExtender.ParseMode(pref.Mode);
        bool isLinux = OperatingSystem.IsLinux();
        string? existingWineOverrides = isLinux ? Environment.GetEnvironmentVariable("WINEDLLOVERRIDES") : null;

        return Task.Run(async () =>
        {
            if (!pref.Enabled)
            {
                // Opt-out guarantee: ensure neither Client/version.dll nor .nmimp remains active on disk so stock runs stay 100% vanilla.
                // Only touch what NativeMods recorded installing here: a version.dll of another tool, or a linked client, is left alone.
                bool ours = WaypointExtenderManager.ReadState(root) is not null && ClientVersions.Layout(root) != ClientLayout.Link;
                bool proxyPresent = ours && File.Exists(WaypointExtenderManager.GetProxyDllPath(root));
                string patchedExe = WaypointExtenderManager.GetPatchedExePath(root);
                bool needsDeactivate = proxyPresent;
                if (ours && !needsDeactivate && File.Exists(patchedExe) && File.Exists(WaypointExtenderManager.GetNativeShimPath(root)))
                {
                    var health = await NativeModsExtender.GetHealthAsync(root, optedIn: false, isReleaseInstall, mode);
                    needsDeactivate = health.PeImportVerified;
                }

                if (needsDeactivate)
                {
                    var deactivated = await NativeModsExtender.SetEnabledAsync(root, enabled: false, mode, isReleaseInstall);
                    if (!deactivated.Succeeded)
                        return new NativeModsLaunchPlan(false, new Dictionary<string, string?>(), deactivated.Message);
                    log?.Invoke("[nativemods] opted-out: deactivated extender entry point for vanilla launch.");
                }

                return new NativeModsLaunchPlan(true, NativeModsExtender.BuildLaunchEnvironment(root, optedIn: false, mode, isLinux, existingWineOverrides));
            }

            // Opt-in ON: verify extender installation, build compatibility, entry point, and .merged-cache freshness.
            var status = await NativeModsExtender.GetHealthAsync(root, optedIn: true, isReleaseInstall, mode);
            if (!status.IsInstalled)
                return new NativeModsLaunchPlan(false, new Dictionary<string, string?>(), L.Text("NativeMods is enabled in Settings, but the extender is not installed. Install it in Settings or turn NativeMods off."));

            if (status.ClientIsLink)
                return new NativeModsLaunchPlan(false, new Dictionary<string, string?>(), L.Text("Cannot run NativeMods with a linked Client folder. Make a copy of the game client or turn NativeMods off in Settings."));

            if (status.ClientBuild is not null && !status.BuildSupported)
                return new NativeModsLaunchPlan(false, new Dictionary<string, string?>(), L.Text("Client build {0} is not supported by NativeMods (supported: 3.29.3c, 3.29.3d).", status.ClientBuild));

            if (isReleaseInstall && mode == ExtenderInstallMode.ImportTable)
                return new NativeModsLaunchPlan(false, new Dictionary<string, string?>(), L.Text("Import patch (.nmimp) mode is not supported on release installs because LocalExile.exe checks Game.patched.exe file size and resets it. Switch to Proxy (version.dll) mode in Settings."));

            if (!status.RuntimeIntegrityOk || !status.NativeShimPresent)
                return new NativeModsLaunchPlan(false, new Dictionary<string, string?>(), L.Text("NativeMods runtime files are incomplete. Reinstall or verify NativeMods in Settings."));

            // Ensure the chosen entry point (version.dll or .nmimp) is active on disk before starting the client.
            bool entryReady = mode == ExtenderInstallMode.ProxyVersionDll ? status.ProxyDllPresent : status.PeImportVerified;
            if (!entryReady)
            {
                var activated = await NativeModsExtender.SetEnabledAsync(root, enabled: true, mode, isReleaseInstall);
                if (!activated.Succeeded)
                    return new NativeModsLaunchPlan(false, new Dictionary<string, string?>(), activated.Message);
                log?.Invoke($"[nativemods] activated {mode} entry point.");
            }

            if (status.MergedCacheStale)
            {
                var rebuilt = NativeModsClientStore.RebuildMergedCacheForEnabledMods(root);
                if (rebuilt.Rebuilt)
                    log?.Invoke($"[nativemods] rebuilt Mods/Client/.merged-cache ({rebuilt.MergedTables.Count} tables, digest {rebuilt.Digest}).");
                else if (!string.IsNullOrWhiteSpace(rebuilt.Diagnostic))
                    log?.Invoke($"[nativemods] warning: {rebuilt.Diagnostic}");
            }

            var env = NativeModsExtender.BuildLaunchEnvironment(root, optedIn: true, mode, isLinux, existingWineOverrides);
            log?.Invoke($"[nativemods] launching with extender v{status.ExtenderVersion ?? "?"} ({mode}).");
            return new NativeModsLaunchPlan(true, env);
        });
    }

    /// <summary>Merges two optional environment override dictionaries (later entries win).</summary>
    public static IReadOnlyDictionary<string, string?>? MergeEnvironment(
        IReadOnlyDictionary<string, string?>? first,
        IReadOnlyDictionary<string, string?>? second)
    {
        if (first is null or { Count: 0 }) return second;
        if (second is null or { Count: 0 }) return first;
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in first) merged[k] = v;
        foreach (var (k, v) in second) merged[k] = v;
        return merged;
    }

    /// <summary>Formats a concise single-line summary and theme brush key for the NativeMods Settings card.</summary>
    public static (string Summary, string BrushKey, string Details) DescribeHealth(NativeModsHealth health)
    {
        string buildPart = health.ClientBuild is { Length: > 0 } b ? L.Text("Client {0}", b) : L.Text("Client build unknown");
        if (!health.IsInstalled)
        {
            string summary = health.OptedIn
                ? L.Text("Not installed · {0}", buildPart)
                : L.Text("Not installed (off by default) · {0}", buildPart);
            return (summary, health.OptedIn ? "Shell.Warning" : "Shell.TextSecondary", string.Join("\n", health.Diagnostics));
        }

        string ver = health.ExtenderVersion ?? "local";
        string state = !health.OptedIn
            ? L.Text("Installed v{0} (disabled) · {1}", ver, buildPart)
            : health.IsHealthy
                ? L.Text("Active v{0} ({1}) · {2} · {3} client mod(s)", ver, NativeModsExtender.ModeLabel(health.Mode), buildPart, health.InstalledClientModCount)
                : L.Text("Installed v{0} (attention needed) · {1}", ver, buildPart);

        string brush = !health.OptedIn ? "Shell.TextSecondary" : health.IsHealthy ? "Shell.Good" : "Shell.Warning";
        if (health.Diagnostics.Count > 0 && health.OptedIn && !health.IsHealthy) brush = "Shell.Error";
        return (state, brush, string.Join("\n", health.Diagnostics));
    }

    /// <summary>Returns an error message if NativeMods files cannot be mutated right now (busy or game running); null when safe.</summary>
    public static string? CannotMutateNow(Shared shared)
    {
        if (shared.IsLiveRunning)
            return L.Text("{0} is still running. Wait for it to finish first.", shared.BusyWith);
        if (shared.Monitor.Latest.Any(s => s.Service == Service.Client && s.Running))
            return L.Text("Close Path of Exile before changing the NativeMods extender.");
        return null;
    }

    /// <summary>Downloads and installs the latest NativeMods GitHub release under <see cref="Shared.RunExclusiveAsync"/>.</summary>
    public static async Task<NativeModsOperationResult?> InstallFromGitHubAsync(Shared shared, Action<string> progress)
    {
        string root = Environment.CurrentDirectory;
        var pref = shared.Settings.NativeModsFor(root);
        bool isRelease = Repo.IsInstall;
        var mode = isRelease ? ExtenderInstallMode.ProxyVersionDll : NativeModsExtender.ParseMode(pref.Mode);
        NativeModsOperationResult? outcome = null;
        bool ran = await shared.RunExclusiveAsync(L.Text("Install NativeMods"), async () =>
        {
            outcome = await Task.Run(async () =>
            {
                progress(L.Text("Checking GitHub for the latest NativeMods release…"));
                using var http = Updates.Client();
                var release = await NativeModsReleases.LatestAsync(http);
                if (release is null)
                {
                    var health = await NativeModsExtender.GetHealthAsync(root, pref.Enabled, isRelease, mode);
                    return new NativeModsOperationResult(false, L.Text("No NativeMods release was found on GitHub ({0}).", NativeModsReleases.Repository), health);
                }
                progress(L.Text("Downloading NativeMods {0}…", release.Tag));
                var staged = await NativeModsReleases.DownloadAndStageAsync(http, release, (done, total) =>
                {
                    if (total > 0) progress(L.Text("Downloading NativeMods {0} ({1:P0})…", release.Tag, (double)done / total));
                }, CancellationToken.None);
                try
                {
                    progress(L.Text("Installing NativeMods {0}…", staged.Version));
                    var installed = await NativeModsExtender.InstallAsync(root, staged, mode, pref.Enabled, isRelease);
                    shared.Logs.Write($"[nativemods] {installed.Message}", installed.Succeeded ? Severity.Info : Severity.Warning);
                    return installed;
                }
                finally { NativeModsReleases.Discard(staged); }
            });
        });
        return ran ? outcome : null;
    }

    /// <summary>Installs NativeMods from a local <c>.zip</c> or <c>dist/win-x64/</c> directory under <see cref="Shared.RunExclusiveAsync"/>.</summary>
    public static async Task<NativeModsOperationResult?> InstallFromLocalAsync(Shared shared, string path, bool isZip, Action<string> progress)
    {
        string root = Environment.CurrentDirectory;
        var pref = shared.Settings.NativeModsFor(root);
        bool isRelease = Repo.IsInstall;
        var mode = isRelease ? ExtenderInstallMode.ProxyVersionDll : NativeModsExtender.ParseMode(pref.Mode);
        NativeModsOperationResult? outcome = null;
        bool ran = await shared.RunExclusiveAsync(L.Text("Install NativeMods"), async () =>
        {
            outcome = await Task.Run(async () =>
            {
                progress(L.Text("Validating NativeMods bundle…"));
                var staged = isZip ? NativeModsReleases.StageLocalZip(path) : NativeModsReleases.StageLocalDirectory(path);
                try
                {
                    progress(L.Text("Installing NativeMods {0}…", staged.Version));
                    var installed = await NativeModsExtender.InstallAsync(root, staged, mode, pref.Enabled, isRelease);
                    shared.Logs.Write($"[nativemods] {installed.Message}", installed.Succeeded ? Severity.Info : Severity.Warning);
                    return installed;
                }
                finally { NativeModsReleases.Discard(staged); }
            });
        });
        return ran ? outcome : null;
    }

    /// <summary>Applies a change to the opt-in toggle or injection mode under <see cref="Shared.RunExclusiveAsync"/> and persists Settings on success.</summary>
    public static async Task<NativeModsOperationResult?> ApplyPreferenceAsync(Shared shared, bool enabled, ExtenderInstallMode mode)
    {
        string root = Environment.CurrentDirectory;
        bool isRelease = Repo.IsInstall;
        NativeModsOperationResult? outcome = null;
        bool ran = await shared.RunExclusiveAsync(L.Text("Update NativeMods"), async () =>
        {
            var current = await Task.Run(() => NativeModsExtender.GetHealthAsync(root, enabled, isRelease, mode));
            if (!current.IsInstalled)
            {
                // Save preference without failing when turning OFF or switching mode before install; refuse enabling until installed.
                if (enabled)
                {
                    outcome = new(false, L.Text("Install the NativeMods extender first before enabling it."), current);
                    return;
                }
                shared.Settings.SetNativeMods(new NativeModsPreference(false, mode.ToString()), root);
                shared.Settings.Save();
                outcome = new(true, L.Text("NativeMods preference saved."), current);
                return;
            }

            outcome = await Task.Run(() => NativeModsExtender.SetEnabledAsync(root, enabled, mode, isRelease));
            if (outcome.Succeeded)
            {
                shared.Settings.SetNativeMods(new NativeModsPreference(enabled, mode.ToString()), root);
                shared.Settings.Save();
                shared.Logs.Write($"[nativemods] {outcome.Message}", Severity.Info);
            }
        });
        return ran ? outcome : null;
    }

    /// <summary>Uninstalls the extender under <see cref="Shared.RunExclusiveAsync"/> and turns opt-in OFF in Settings.</summary>
    public static async Task<NativeModsOperationResult?> UninstallAsync(Shared shared, bool removeClientMods)
    {
        string root = Environment.CurrentDirectory;
        bool isRelease = Repo.IsInstall;
        NativeModsOperationResult? outcome = null;
        bool ran = await shared.RunExclusiveAsync(L.Text("Uninstall NativeMods"), async () =>
        {
            outcome = await Task.Run(() => NativeModsExtender.UninstallAsync(root, removeClientMods, isRelease));
            if (outcome.Succeeded)
            {
                var pref = shared.Settings.NativeModsFor(root);
                shared.Settings.SetNativeMods(pref with { Enabled = false }, root);
                shared.Settings.Save();
                shared.Logs.Write($"[nativemods] {outcome.Message}", Severity.Info);
            }
        });
        return ran ? outcome : null;
    }
}
