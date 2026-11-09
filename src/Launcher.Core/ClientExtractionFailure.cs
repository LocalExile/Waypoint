using System.Security.Cryptography;

namespace Launcher.Core;

/// <summary>Read-only support details for the executable actually consumed by client-constants extraction.</summary>
public static class ClientExtractionFailure
{
    public static bool IsHncFailure(string? message) =>
        message?.Contains("HNC mask not found", StringComparison.OrdinalIgnoreCase) == true;

    public static string RecoveryHint => L.Text("Client setup needs attention. Run setup guide below to recheck and refresh it, then try Play again.");

    public static string Describe(string checkout, string failure)
    {
        string client = Path.GetFullPath(Path.Combine(checkout, "Client"));
        string log = Path.GetFullPath(Path.Combine(checkout, "logs", "extract-client-constants.log"));
        string details = string.Join("\n\n", new[] { "Game.exe", "PathOfExile.exe" }.Select(name => Fingerprint(Path.Combine(client, name))));
        return L.Text("LocalExile could not read the expected client constants from:\n{0}\n\nSetup can identify PathOfExile.exe while extraction reads Game.exe in this LocalExile folder. Run the setup guide again to check and prepare this copy. If it still fails, share the details below and this log:\n{1}\n\n{2}",
            Path.Combine(client, "Game.exe"), log, details) + "\n\n" + failure;
    }

    static string Fingerprint(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return $"{path}\nbytes={stream.Length}; SHA-256={Convert.ToHexStringLower(SHA256.HashData(stream))}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path + "\n" + e.Message;
        }
    }
}
