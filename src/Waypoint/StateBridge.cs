using System.Diagnostics;
using System.Text;

namespace Waypoint;

/// <summary>
/// The client-state bridge (scripts/StateBridge.py): while the client runs it attaches with Frida and feeds it the
/// documents the server publishes on its ClientState channels (data/clients/state/&lt;channel&gt;.json). The first
/// channel is the Simulacrum wave's reward tiles. Opt-in (<see cref="Settings.StateBridge"/>); it waits for the client
/// to start and ends with it, and never touches the game from another thread.
/// </summary>
sealed class StateBridge : IDisposable
{
    readonly StringBuilder _errors = new();
    Process? _process;

    public static StateBridge? Start(Action<string> output)
    {
        string bridge = Path.Combine(Path.GetDirectoryName(BridgeScripts.Extract())!, "StateBridge.py");
        var info = new ProcessStartInfo(Repo.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        info.ArgumentList.Add(bridge);
        var state = new StateBridge();
        try
        {
            state._process = Process.Start(info);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            output(L.Text("The state bridge could not start: {0}", e.Message));
            return null;
        }
        if (state._process is null)
        {
            output(L.Text("The state bridge could not start."));
            return null;
        }
        state._process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) output($"state: {line}"); };
        state._process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line) return;
            lock (state._errors) state._errors.AppendLine(line);
            output($"state: {line}");
        };
        state._process.EnableRaisingEvents = true;
        state._process.BeginOutputReadLine();
        state._process.BeginErrorReadLine();
        return state;
    }

    public void Dispose()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }
}