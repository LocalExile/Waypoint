using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Waypoint;

/// <summary>The one failure of the bridge that a press of a button can mend.</summary>
sealed class FridaMissingException(string message) : InvalidOperationException(message);

/// <summary>
/// The native client control bridge (scripts/dev/client_control.py --stdio, Agents/docs/dev/client-control.md) as one
/// persistent process: a JSON request per line, a JSON result per line. Commands reach the Gateway as the client's own
/// chat message (0x15), exactly as typed in the chat box, and the result carries the Gateway's chat replies.
/// </summary>
sealed class ClientBridge : IDisposable
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly StringBuilder _errors = new();
    Process? _process;

    public async Task<JsonObject> RequestAsync(JsonObject request, TimeSpan timeout)
    {
        await _gate.WaitAsync();
        try
        {
            var process = Ensure();
            await process.StandardInput.WriteLineAsync(request.ToJsonString());
            await process.StandardInput.FlushAsync();
            string? line;
            try { line = await process.StandardOutput.ReadLineAsync().WaitAsync(timeout); }
            catch (TimeoutException)
            {
                // A result that comes later would answer the next request: start over.
                Stop();
                throw new TimeoutException(L.Text("no answer from the bridge in {0:0} s", timeout.TotalSeconds));
            }
            if (line is null)
            {
                process.WaitForExit(2000);
                string error;
                lock (_errors) error = _errors.ToString();
                Stop();
                // Frida is optional for playing, so this one is expected and has a way out.
                if (error.Contains("No module named 'frida'", StringComparison.Ordinal))
                    throw new FridaMissingException(L.Text("the bridge needs Python with frida, which is not installed: Give item in the item crafter sets it up"));
                throw new InvalidOperationException(L.Text("the bridge stopped: {0}", Tail(error)));
            }
            return JsonNode.Parse(line)?.AsObject() ?? throw new InvalidOperationException(L.Text("not a result: {0}", line));
        }
        finally { _gate.Release(); }
    }

    /// <summary>A slash command (or any chat line) and the Gateway's replies.</summary>
    public Task<JsonObject> SendAsync(string text, double wait = 1.5) => RequestAsync(new JsonObject
    {
        ["command"] = text.StartsWith('/') ? "command" : "chat",
        ["text"] = text,
        ["wait"] = wait,
    }, TimeSpan.FromSeconds(20));

    public Task<JsonObject> StatusAsync() =>
        RequestAsync(new JsonObject { ["command"] = "game:status" }, TimeSpan.FromSeconds(20));

    Process Ensure()
    {
        if (_process is { HasExited: false }) return _process;
        Stop();
        var info = new ProcessStartInfo(Repo.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        info.ArgumentList.Add(Path.GetFullPath(Repo.ClientControl));
        info.ArgumentList.Add("--stdio");
        info.Environment["PYTHONUTF8"] = "1";
        try
        {
            lock (_errors) _errors.Clear();
            _process = Process.Start(info)!;
            // Read all along, so that a chatty stderr can't fill its pipe and stall the bridge.
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_errors)
                {
                    _errors.AppendLine(e.Data);
                    if (_errors.Length > 8000) _errors.Remove(0, _errors.Length - 8000);
                }
            };
            _process.BeginErrorReadLine();
            return _process;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException(L.Text("can't start {0} ({1}): the bridge needs Python with frida (.venv, GettingStarted step 9)", Repo.Python, e.Message));
        }
    }

    static string Tail(string text) => text.Trim() is { Length: > 600 } t ? "…" + t[^600..] : text.Trim();

    void Stop()
    {
        if (_process is null) return;
        try
        {
            // Closing stdin detaches the bridge; the game keeps running.
            _process.StandardInput.Close();
            if (!_process.WaitForExit(2000)) _process.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        _process.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();

    /// <summary>The replies of a command result, or its error.</summary>
    public static (bool Ok, IReadOnlyList<string> Lines) Replies(JsonObject result)
    {
        if (result["ok"]?.GetValue<bool>() != true) return (false, [result["error"]?.GetValue<string>() ?? result.ToJsonString()]);
        if (result["result"]?["replies"] is JsonArray replies)
            return (true, [.. replies.Select(r => r?.GetValue<string>() ?? "")]);
        return (true, []);
    }
}
