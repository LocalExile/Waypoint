using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Waypoint;

/// <summary>
/// The dev account service (GameServer.Host, GettingStarted step 8): the launcher registers accounts and creates
/// characters through its JSON-lines protocol (Agents/docs/dev-server.md), as GameServer.DevClient does. Started on
/// demand like live.ps1 starts the servers (detached, output in logs/launcher-host.log), and left running: it shares
/// data/saves.db with Auth, and neither caches it.
/// </summary>
static class DevHost
{
    static int Port => Service.DevHost.Ports[0];

    public static bool Listening() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == Port);

    public static async Task EnsureRunningAsync(Action<string> output)
    {
        if (Listening()) return;
        output(L.Text("building GameServer.Host …"));
        var build = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in (string[])["build", Path.Combine("src", "GameServer.Host"), "-v", "q", "-nologo"]) build.ArgumentList.Add(a);
        build.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        build.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        using (var p = Process.Start(build)!)
        {
            string text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new InvalidOperationException(L.Text("build failed: GameServer.Host\n{0}", text.Trim()));
        }
        Directory.CreateDirectory(Repo.Logs);
        // As live.ps1's Start-Detached: through cmd's redirection, inheriting no handles of ours.
        string log = Path.Combine(Repo.Logs, "launcher-host.log"), err = Path.Combine(Repo.Logs, "launcher-host.err");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c dotnet \"{Repo.ServerDll("Host")}\" 1>\"{log}\" 2>\"{err}\"")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.CurrentDirectory,
        })?.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!Listening())
        {
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException(L.Text("the dev host did not start (logs\\launcher-host.err): {0}", File.Exists(err) ? File.ReadAllText(err).Trim() : ""));
            await Task.Delay(250);
        }
        output(L.Text("dev host listening on 127.0.0.1:{0}", Port));
    }

    /// <summary>Requests on one connection (a login holds for the rest of it); the replies, or the first refusal as an
    /// exception.</summary>
    public static async Task<List<JsonObject>> RunAsync(params JsonObject[] requests)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", Port).WaitAsync(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var replies = new List<JsonObject>();
        foreach (var request in requests)
        {
            await writer.WriteLineAsync(request.ToJsonString());
            string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
                ?? throw new InvalidOperationException(L.Text("the dev host closed the connection"));
            var reply = JsonNode.Parse(line)!.AsObject();
            if (reply["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"{request["op"]}: {reply["error"]?.ToString() ?? line}");
            replies.Add(reply);
        }
        return replies;
    }

    public static Task Register(string name, string password) =>
        RunAsync(new JsonObject { ["op"] = "register", ["name"] = name, ["password"] = password });

    public static Task CreateCharacter(string account, string password, string name, string characterClass) =>
        RunAsync(new JsonObject { ["op"] = "login", ["name"] = account, ["password"] = password },
            new JsonObject { ["op"] = "create_character", ["name"] = name, ["class"] = characterClass });
}
