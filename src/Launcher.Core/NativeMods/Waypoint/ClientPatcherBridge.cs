using LocalExile.Client.Patcher;

namespace LocalExile.Client.Waypoint;

/// <summary>
/// Concrete <see cref="IPePatcherBridge"/> adapter delegating directly to <see cref="ClientPatcher"/>
/// so Waypoint's <see cref="WaypointExtenderManager"/> drives the real PE32+ import-table patcher.
/// </summary>
public sealed class ClientPatcherBridge : IPePatcherBridge
{
    private readonly ClientPatcher _patcher;
    private readonly bool _allowSynthetic;
    private readonly bool _createBackup;
    private readonly string _importAnchorSymbol;

    public ClientPatcherBridge(
        AddressLibraryCatalog? catalog = null,
        bool allowSynthetic = false,
        bool createBackup = false,
        string importAnchorSymbol = ClientPatcher.DefaultImportAnchorSymbol)
        : this(new ClientPatcher(catalog), allowSynthetic, createBackup, importAnchorSymbol)
    {
    }

    public ClientPatcherBridge(
        ClientPatcher patcher,
        bool allowSynthetic = false,
        bool createBackup = false,
        string importAnchorSymbol = ClientPatcher.DefaultImportAnchorSymbol)
    {
        _patcher = patcher ?? throw new ArgumentNullException(nameof(patcher));
        _allowSynthetic = allowSynthetic;
        _createBackup = createBackup;
        _importAnchorSymbol = string.IsNullOrWhiteSpace(importAnchorSymbol)
            ? ClientPatcher.DefaultImportAnchorSymbol
            : importAnchorSymbol;
    }

    public Task<PePatchResult> CheckAsync(string patchedExePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(patchedExePath))
            return Task.FromResult(new PePatchResult(false, $"Executable not found: '{patchedExePath}'."));

        try
        {
            byte[] bytes = File.ReadAllBytes(patchedExePath);
            var check = _patcher.CheckBytes(bytes, allowSynthetic: _allowSynthetic);
            bool recognized = check.IsValidPe32Plus && check.MatchKind != BuildMatchKind.Unknown;
            return Task.FromResult(new PePatchResult(
                Succeeded: recognized,
                Message: check.Message,
                IsPatched: check.HasNativeModsImport,
                DetectedBuild: check.GameVersion ?? (check.MatchKind == BuildMatchKind.SyntheticAllowed ? "synthetic" : null),
                ExecutableSha256: check.Sha256Hex));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return Task.FromResult(new PePatchResult(false, ex.Message));
        }
    }

    public Task<PePatchResult> PatchAsync(string patchedExePath, string hostDllName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(patchedExePath))
            return Task.FromResult(new PePatchResult(false, $"Executable not found: '{patchedExePath}'."));

        try
        {
            var options = new PatchOptions(
                DllName: hostDllName,
                ImportAnchorSymbol: _importAnchorSymbol,
                AllowSynthetic: _allowSynthetic,
                CreateBackup: _createBackup);
            var result = _patcher.PatchFile(patchedExePath, patchedExePath, options);
            return Task.FromResult(new PePatchResult(
                Succeeded: result.Patched || result.AlreadyPatched,
                Message: result.Message,
                IsPatched: true,
                DetectedBuild: result.GameVersion ?? (result.MatchKind == BuildMatchKind.SyntheticAllowed ? "synthetic" : null),
                ExecutableSha256: result.OutputSha256));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return Task.FromResult(new PePatchResult(false, ex.Message));
        }
    }

    public Task<PePatchResult> UnpatchAsync(string patchedExePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(patchedExePath))
            return Task.FromResult(new PePatchResult(false, $"Executable not found: '{patchedExePath}'."));

        try
        {
            var result = _patcher.UnpatchFile(patchedExePath, patchedExePath);
            return Task.FromResult(new PePatchResult(
                Succeeded: result.Unpatched && result.ExactByteRestoreVerified,
                Message: result.Message,
                IsPatched: false,
                DetectedBuild: null,
                ExecutableSha256: result.RestoredSha256));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return Task.FromResult(new PePatchResult(false, ex.Message));
        }
    }

    public Task<PePatchResult> VerifyAsync(string patchedExePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(patchedExePath))
            return Task.FromResult(new PePatchResult(false, $"Executable not found: '{patchedExePath}'."));

        try
        {
            byte[] bytes = File.ReadAllBytes(patchedExePath);
            var verify = _patcher.VerifyBytes(bytes);
            return Task.FromResult(new PePatchResult(
                Succeeded: verify.IsValid || !verify.HasNativeModsImport,
                Message: verify.Message,
                IsPatched: verify.IsValid && verify.HasNativeModsImport,
                DetectedBuild: null,
                ExecutableSha256: verify.Sha256Hex));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return Task.FromResult(new PePatchResult(false, ex.Message));
        }
    }
}
