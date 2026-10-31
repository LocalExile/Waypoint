using System.Reflection;
using System.Security.Cryptography;

namespace Launcher.Core;

/// <summary>The adapter's scripts travel inside this assembly, so the one file a self-update replaces carries them. They
/// run from a folder named after their content under the data directory; a new version gets its own folder.</summary>
public static class BridgeScripts
{
    const string Prefix = "Bridge/";

    /// <summary>Unpacks the scripts when this version's folder is not there yet; the path of RuntimeBridge.ps1.</summary>
    public static string Extract()
    {
        var assembly = typeof(BridgeScripts).Assembly;
        var files = assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .ToDictionary(n => n[Prefix.Length..], n => Read(assembly, n));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, bytes) in files)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            hash.AppendData(bytes);
        }
        string folder = Path.Combine(SettingsStore.DataDirectory, "adapter", Convert.ToHexStringLower(hash.GetHashAndReset())[..16]);
        string bridge = Path.Combine(folder, "RuntimeBridge.ps1");
        if (File.Exists(bridge)) return bridge;
        string staging = folder + "." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(staging, name), bytes);
        try { Directory.Move(staging, folder); }
        // Another copy of this version unpacked the same files first.
        catch (IOException) when (File.Exists(bridge)) { Directory.Delete(staging, true); }
        return bridge;
    }

    static byte[] Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new IOException($"The adapter script {name} is missing from this build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
