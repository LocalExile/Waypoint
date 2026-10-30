using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public sealed record OperationProgress(string Stage, string Detail, string LogPath);

/// <summary>Interprets upstream extraction messages; it does not invent a percentage or estimate completion.</summary>
public sealed class OperationProgressReader
{
    readonly StringBuilder pending = new();
    public IEnumerable<string> Append(string text)
    {
        pending.Append(text);
        var stages = new List<string>();
        string buffer = pending.ToString();
        int end;
        while ((end = buffer.IndexOf('\n')) >= 0)
        {
            string line = buffer[..end].Trim();
            buffer = buffer[(end + 1)..];
            var match = Regex.Match(line, @"^extracting ([\w-]+)\s", RegexOptions.CultureInvariant);
            if (match.Success) stages.Add("Extracting " + match.Groups[1].Value.Replace('-', ' '));
        }
        pending.Clear().Append(buffer.Length > 4096 ? buffer[^4096..] : buffer);
        return stages;
    }
}
