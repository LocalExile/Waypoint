namespace Launcher.Core;

public static class RepositoryNaming
{
    public static string FolderName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Workspace";
        try
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path.Trim()));
            return string.IsNullOrWhiteSpace(name) ? "Workspace" : name;
        }
        catch (ArgumentException) { return "Workspace"; }
    }

    public static string NameFromRemote(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return "";
        string path = remote.Trim();
        if (path.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ssh" or "git" or "file")) return "";
            path = Uri.UnescapeDataString(uri.AbsolutePath); // Never display credentials, hosts, query strings or fragments.
        }
        else
        {
            int colon = path.IndexOf(':');
            if (colon >= 0 && !(colon == 1 && char.IsLetter(path[0]))) path = path[(colon + 1)..]; // SCP-style SSH remote.
        }
        string name = path.TrimEnd('/', '\\').Replace('\\', '/').Split('/').Last();
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Length == 0 || name is "." or ".." || name.Any(char.IsControl) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "" : name;
    }

    public static async Task<string> ResolveAsync(string root, CancellationToken cancellation = default)
    {
        string fallback = FolderName(root);
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git"))) return fallback;
        // Read the selected checkout's origin locally. This does not contact the remote or change Git configuration.
        var result = await GitRepositoryProbe.ReadAsync(root, ["config", "--get", "remote.origin.url"], TimeSpan.FromSeconds(3), cancellation);
        string name = result.ExitCode == 0 ? NameFromRemote(result.Output) : "";
        return name.Length > 0 ? name : fallback;
    }
}
