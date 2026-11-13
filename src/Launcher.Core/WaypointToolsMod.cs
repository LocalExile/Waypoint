using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Launcher.Core;

/// <summary>The answer to one request: whether it was carried out, and the mod's words for it.</summary>
public sealed record ToolsReply(bool Ok, string Message);

/// <summary>
/// Waypoint's side of the Waypoint Tools mod (LocalExile-Mods' <c>mods/waypoint-tools</c>): the way the item crafter gives an
/// item without the client control bridge, so it works on a release too. Through files in the LocalExile folder, never the
/// network: the mod writes <c>data/waypoint-tools/loaded.json</c> while a Gateway has it loaded, a request goes into
/// <c>requests/&lt;id&gt;.json</c>, and its answer comes back in <c>replies/&lt;id&gt;.json</c>. The paths are relative to
/// <paramref name="root"/>, the checkout or install.
/// </summary>
public static class WaypointToolsMod
{
    /// <summary>The mod's id in LocalExile-Mods, its folder under <c>Mods/Binaries/</c>.</summary>
    public const string Id = "waypoint-tools";

    /// <summary>The version of the request and reply files this Waypoint writes and reads.</summary>
    public const int Protocol = 1;

    static string Root(string root) => Path.Combine(root, "data", Id);

    /// <summary>Whether the mod is installed in this folder (it may still be turned off, or no Gateway runs).</summary>
    public static bool Installed(string root) => File.Exists(Path.Combine(root, "Mods", "Binaries", Id, "mod.json"));

    /// <summary>Whether a running Gateway has the mod loaded with this action: its <c>loaded.json</c> is of this protocol,
    /// lists the action, and names a process that still runs (one left by a Gateway that stopped does not count).</summary>
    public static bool Loaded(string root, string action, Func<int, bool>? running = null)
    {
        try
        {
            string file = Path.Combine(Root(root), "loaded.json");
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json) return false;
            return (int?)json["protocol"] == Protocol
                && json["actions"] is JsonArray actions && actions.Any(a => (string?)a == action)
                && (int?)json["pid"] is int pid && (running ?? Running)(pid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>What <c>loaded.json</c> says now, to keep across a question to a Gateway (<see cref="KeepLoaded"/>); null
    /// without the file.</summary>
    public static string? Announced(string root)
    {
        try
        {
            string file = Path.Combine(Root(root), "loaded.json");
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// After a Gateway was asked a question: puts <c>loaded.json</c> back as it was <paramref name="before"/>, when the
    /// asked process wrote itself into it over a Gateway that still runs. A Gateway's <c>commands</c> and <c>values</c>
    /// modes load the mods too, for their commands, and the mod up to 1.0.2 then named that process, which ends with the
    /// question: the Gateway that runs the game still had the mod loaded, but Give item took it for not loaded (Waypoint's
    /// Console asks when Waypoint starts, so a Waypoint started while the game ran always did). True when it put it back.
    /// </summary>
    public static bool KeepLoaded(string root, string? before, int asked, Func<int, bool>? running = null)
    {
        try
        {
            if (before is null || Pid(before) is not int owner || owner == asked) return false;
            string file = Path.Combine(Root(root), "loaded.json");
            if (!File.Exists(file) || Pid(File.ReadAllText(file)) != asked || !(running ?? Running)(owner)) return false;
            // Beside, then moved into place: the mod and Waypoint never read half of it.
            File.WriteAllText(file + ".tmp", before);
            File.Move(file + ".tmp", file, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The process a <c>loaded.json</c> names, or null when it names none.</summary>
    static int? Pid(string json)
    {
        try { return JsonNode.Parse(json) is JsonObject o ? (int?)o["pid"] : null; }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Gives an item (<paramref name="item"/>: what <c>/item_give</c> takes, one JSON object or an array) to
    /// <paramref name="character"/>, or with null to the one character in game. Waits for the mod's answer; when none comes
    /// in time, takes the request back if the mod has not read it yet and answers that nothing came.</summary>
    public static async Task<ToolsReply> GiveItemAsync(string root, string item, string? character, TimeSpan timeout, CancellationToken stop = default)
    {
        var request = new JsonObject { ["action"] = "item_give", ["item"] = JsonNode.Parse(item) };
        if (!string.IsNullOrEmpty(character)) request["character"] = character;
        return await AskAsync(root, request, timeout, stop);
    }

    /// <summary>Writes one request (beside, then moved into place, so the mod never reads half of it) and waits for its reply.</summary>
    internal static async Task<ToolsReply> AskAsync(string root, JsonObject request, TimeSpan timeout, CancellationToken stop)
    {
        string requests = Path.Combine(Root(root), "requests"), replies = Path.Combine(Root(root), "replies");
        Directory.CreateDirectory(requests);
        string id = Guid.NewGuid().ToString("D");
        string file = Path.Combine(requests, id + ".json"), reply = Path.Combine(replies, id + ".json");
        await File.WriteAllTextAsync(Path.Combine(requests, id + ".tmp"), request.ToJsonString(), stop);
        File.Move(Path.Combine(requests, id + ".tmp"), file);
        var deadline = Stopwatch.StartNew();
        try
        {
            while (deadline.Elapsed < timeout)
            {
                if (Read(reply) is { } answer)
                {
                    TryDelete(reply);
                    return answer;
                }
                await Task.Delay(100, stop);
            }
        }
        catch (OperationCanceledException) { TryDelete(file); throw; }
        // The mod may be giving it right now: an answer written meanwhile still counts.
        TryDelete(file);
        if (Read(reply) is { } late) { TryDelete(reply); return late; }
        return new(false, L.Text("Waypoint Tools did not answer. Is the game running, with a character in an area?"));
    }

    /// <summary>A reply file, or null while it is not there or not readable yet.</summary>
    internal static ToolsReply? Read(string file)
    {
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json) return null;
            return new((bool?)json["ok"] == true, (string?)json["message"] ?? "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
