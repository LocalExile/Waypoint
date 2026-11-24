using Launcher.Core;

namespace Waypoint.Tests;

// The state bridge runs whenever the client does (scripts/StateBridge.py with its agent); it travels inside the build.
public class StateBridgeTests
{
    [Fact]
    public void The_adapter_carries_the_state_bridge()
    {
        string folder = Path.Combine(Path.GetTempPath(), "waypoint-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            string bridge = BridgeScripts.Extract(folder);
            string dir = Path.GetDirectoryName(bridge)!;
            Assert.True(File.Exists(Path.Combine(dir, "StateBridge.py")));
            Assert.True(File.Exists(Path.Combine(dir, "StateBridge.js")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}