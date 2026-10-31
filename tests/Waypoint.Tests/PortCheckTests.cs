using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Waypoint.Tests;

public class PortCheckTests
{
    [Fact]
    public void What_listens_on_a_port_is_found_with_its_process()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var holder = Assert.Single(PortCheck.Find([port], []));
            Assert.Equal(Environment.ProcessId, holder.Pid);
            Assert.Equal([port], holder.Ports);
            Assert.NotEqual("?", holder.Name);
            Assert.Contains(port.ToString(), holder.Description);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void A_service_of_this_checkout_does_not_count()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { Assert.Empty(PortCheck.Find([((IPEndPoint)listener.LocalEndpoint).Port], [Environment.ProcessId])); }
        finally { listener.Stop(); }
    }

    [Fact]
    public void A_free_port_has_no_holder()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.Empty(PortCheck.Find([port], []));
    }

    [Theory]
    [InlineData(4, null, false)]
    [InlineData(1234, @"C:\Windows\System32\svchost.exe", false)]
    [InlineData(1234, @"C:\Program Files\dotnet\dotnet.exe", true)]
    public void Windows_itself_is_never_offered_for_ending(int pid, string? path, bool canEnd) =>
        Assert.Equal(canEnd, new PortHolder(pid, 1, "x.exe", path, "", default, [6112]).CanEnd);

    [Theory]
    [InlineData("dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", @"dotnet D:\Other\bin\GameServer.Gateway.dll", @"D:\Other\bin")]
    [InlineData("dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe", @"""dotnet"" src\GameServer.Gateway\bin\GameServer.Gateway.dll commands", null)]
    [InlineData("nginx.exe", @"C:\Tools\nginx\nginx.exe", "nginx", @"C:\Tools\nginx")]
    public void Checking_first_goes_to_where_the_program_lives(string name, string path, string commandLine, string? folder) =>
        Assert.Equal(folder, new PortHolder(1234, 1, name, path, commandLine, default, [6112]).Folder);

    [Fact]
    public async Task Ending_a_holder_ends_its_process()
    {
        using var process = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        await PortCheck.EndAsync(new PortHolder(process.Id, 0, "PING.EXE", null, "ping", default, [6112]), _ => { });
        Assert.True(process.HasExited);
    }
}
