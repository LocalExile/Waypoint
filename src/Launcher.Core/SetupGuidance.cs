namespace Launcher.Core;

public sealed record SetupStep(string Name, bool Ready, string Detail, string Action, string ActionLabel, string Instructions)
{
    public string Symbol => Ready ? "✓" : "!";
    public string Color => Ready ? "#A4E7C4" : "#FFCB87";
    public string DisplayName => L.Text(Name);
    public string DisplayDetail => L.Describe(Detail);
}

public static class SetupGuidance
{
    public static SetupStep Describe(Requirement requirement, string root)
    {
        string client = Path.Combine(root, "Client");
        var (action, label, instructions) = requirement.Name switch
        {
            ".NET 10 SDK" => ("Dotnet", L.Text("Get SDK"), L.Text("Install the .NET 10 SDK for Windows x64 from Microsoft's download page. Close and reopen the launcher after installing, then Recheck setup.")),
            "Python" => ("Python", L.Text("Get Python"), L.Text("Install Python 3.9 or newer from python.org with its PATH option enabled. Close and reopen the launcher, then Recheck setup. Frida is optional and is not required for ordinary play.")),
            "Node.js" => ("Node", L.Text("Get Node.js"), L.Text("Install Node.js (the LocalExile setup guide uses Node 24). Close and reopen the launcher after installing, then Recheck setup.")),
            "Bundle decompressor" => ("Dependencies", L.Text("Install dependency"), L.Text("This installs the checkout's locked Node dependency in Tools/ooz using npm ci. It does not install software globally. Node.js must be installed first.")),
            "Original client" or "Client archive" => ("ClientFolder", L.Text("Client instructions"), L.Text("Copy the complete standalone PoE 3.29.3b installation to:\n{0}\n\n1. In File Explorer, open the supported client folder containing Content.ggpk and PathOfExile.exe (or Game.exe).\n2. Press Ctrl+A, then Ctrl+C to copy; do not move your normal installation.\n3. Open the selected checkout in another File Explorer window. Create its Client folder if missing, open it, then press Ctrl+V.\n4. Wait for the copy to finish. Return to Setup and click Prepare local client.\n\nSteam layout and a newer official client are unsupported. The launcher does not download the game.", client)),
            "Prepared client" or "Login keys" => ("PrepareClient", L.Text("Prepare client"), L.Text("Prepare the existing client copy using LocalExile's Auth and Patch tools. Existing keys are retained. Missing keys and Game.patched.exe are created, and the copy's gateway list is set to localhost. Existing prepared executables are never overwritten.")),
            "Existing save database" => ("Guide", L.Text("View instructions"), L.Text("The selected local runtime profile names a database which does not exist. Restore the intended database or correct config/runtime.local.ps1. Do not create an empty replacement for an existing installation. Open Settings to select a different checkout/profile if needed.")),
            _ => ("Guide", L.Text("View setup guide"), L.Text("Open Agents/GettingStarted.md in the selected checkout. Use the supported upstream launch script and standard client/build paths. Then Recheck setup."))
        };
        return new(requirement.Name, requirement.Ready, requirement.Detail, action, label, instructions);
    }
}
