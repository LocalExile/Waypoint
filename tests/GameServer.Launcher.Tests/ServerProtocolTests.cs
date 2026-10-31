using System.Text.Json.Nodes;
using GameServer.Launcher;

namespace GameServer.Launcher.Tests;

// What the launcher reads from the server checkout: the dev host's account list and the processes it runs.
public class ServerProtocolTests
{
    [Fact]
    public void The_dev_hosts_account_list_becomes_the_accounts_tab()
    {
        var reply = JsonNode.Parse("""
            {"ok":true,"accounts":[
              {"name":"alice","created":"2026-10-29T00:07:41+00:00","characters":[
                {"name":"Exile","class":"Templar","level":12,"league":"Standard","area":"1_1_1","last_town":"1_1_town",
                 "last_played":"2026-10-30T03:26:40+00:00","dead":null},
                {"name":"Gone","class":"Witch","level":30,"league":"Void","area":"1_1_1","last_town":null,
                 "last_played":"2026-10-30T03:24:02+00:00","dead":"permanent"},
                {"name":"Moved","class":"Ranger","level":40,"league":"Standard","area":"1_1_1","last_town":null,
                 "last_played":"2026-10-30T03:24:02+00:00","dead":"Hardcore"}]},
              {"name":"zed","created":"2026-10-29T00:07:41+00:00","characters":[]}]}
            """)!.AsObject();

        var accounts = AccountList.Parse(reply);

        Assert.Equal(["alice", "zed"], accounts.Select(a => a.Name));
        Assert.Equal(3, accounts[0].Characters);
        var exile = accounts[0].CharacterRows[0];
        Assert.Equal(("Exile", "Templar", 12, "Standard", "1_1_town", ""), (exile.Name, exile.Class, exile.Level, exile.League, exile.LastTown, exile.State));
        Assert.Equal("(never in town)", accounts[0].CharacterRows[1].LastTown);
        Assert.Equal("dead (permanent)", accounts[0].CharacterRows[1].State);
        Assert.Equal("died in Hardcore", accounts[0].CharacterRows[2].State);
        Assert.Empty(accounts[1].CharacterRows);
        Assert.Empty(AccountList.Parse(new JsonObject { ["ok"] = true }));
    }

    [Fact]
    public void A_hot_reloaded_gateway_is_its_own_executable_before_the_dotnet_watch_around_it()
    {
        var gateway = Service.Gateway;
        Assert.Equal(2, gateway.Matches("GameServer.Gateway.exe", "\"D:\\LocalExile\\src\\GameServer.Gateway\\bin\\Debug\\net10.0\\GameServer.Gateway.exe\""));
        Assert.Equal(1, gateway.Matches("dotnet.exe", "dotnet  src\\GameServer.Gateway\\bin\\Debug\\net10.0\\GameServer.Gateway.dll"));
        Assert.Equal(0, gateway.Matches("dotnet.exe", "dotnet  watch run --project src\\GameServer.Gateway --non-interactive --"));
        // The launcher's own catalog reads.
        Assert.Equal(0, gateway.Matches("dotnet.exe", "\"dotnet\" src\\GameServer.Gateway\\bin\\Debug\\net10.0\\GameServer.Gateway.dll commands"));
        Assert.Equal(0, gateway.Matches("dotnet.exe", "dotnet \"src\\GameServer.Gateway\\bin\\Debug\\net10.0\\GameServer.Gateway.dll\" values area"));
        Assert.Equal(0, Service.Auth.Matches("GameServer.Gateway.exe", null));
        Assert.Equal(1, Service.Client.Matches("Game.patched.exe", null));
        Assert.Equal(1, Service.Recorder.Matches("python.exe", "python scripts\\walkmaps\\walkmaps.py"));
    }
}
