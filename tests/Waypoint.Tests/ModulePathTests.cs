namespace Waypoint.Tests;

/// <summary>Waypoint started from a PowerShell 7 shell inherits a module path that breaks the scripts Windows PowerShell
/// runs for it (<see cref="Platform.WithoutPowerShell7"/>): PowerShell 7's own three folders go, and nothing else.</summary>
public class ModulePathTests : IDisposable
{
    readonly string _top = Path.Combine(Path.GetTempPath(), "Waypoint-modules-" + Guid.NewGuid().ToString("N"));
    string User => Path.Combine(_top, "Documents", "PowerShell", "Modules");
    string Shared => Path.Combine(_top, "Program Files", "PowerShell", "Modules");
    const string Windows = @"C:\Program Files\WindowsPowerShell\Modules", System = @"C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules";

    public ModulePathTests() => Directory.CreateDirectory(_top);

    public void Dispose() => Directory.Delete(_top, recursive: true);

    /// <summary>A PowerShell 7 install, wherever it is: its Modules folder has a pwsh beside it.</summary>
    string Install(string name, string program)
    {
        string home = Path.Combine(_top, name);
        Directory.CreateDirectory(Path.Combine(home, "Modules"));
        File.WriteAllText(Path.Combine(home, program), "");
        return Path.Combine(home, "Modules");
    }

    [Fact]
    public void A_PowerShell_7_shells_own_folders_go_and_everything_else_stays_in_its_order()
    {
        string seven = Install("PowerShell 7", "pwsh.exe"), tool = Install("dotnet tool", "pwsh.dll");
        // A product's own module folder can be named PowerShell too: it is not PowerShell 7's.
        string product = Path.Combine(_top, "SQL Server", "Tools", "PowerShell", "Modules");
        Directory.CreateDirectory(product);
        string inherited = string.Join(';', User, Shared.ToUpperInvariant() + @"\", seven, Windows, product, System, tool);
        Assert.Equal(string.Join(';', Windows, product, System), Platform.WithoutPowerShell7(inherited, User, Shared));
    }

    [Fact]
    public void A_module_path_that_is_Windows_PowerShells_own_is_not_touched()
    {
        string own = Windows + ";" + System;
        Assert.Same(own, Platform.WithoutPowerShell7(own, User, Shared));
        Assert.Null(Platform.WithoutPowerShell7(null, User, Shared));
        Assert.Equal("", Platform.WithoutPowerShell7("", User, Shared));
    }

    [Fact]
    public void With_nothing_left_there_is_no_module_path_and_Windows_PowerShell_builds_its_own()
    {
        Assert.Null(Platform.WithoutPowerShell7(User + ";" + Install("PowerShell 7", "pwsh.exe"), User, Shared));
    }
}
