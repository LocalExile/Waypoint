using System.Net;
using System.Net.Http;
using Waypoint;

namespace Waypoint.Tests;

public class ModIconTests : IDisposable
{
    // A real 1x1 PNG.
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    static readonly ModListing Listing = new("alpha", "Alpha", "", "", "1.0.0", "feature", false, "mods/alpha", [], false);

    readonly string _root = Path.Combine(Path.GetTempPath(), "waypoint-mod-icons-" + Guid.NewGuid().ToString("N"));

    sealed class Answer(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
        }
    }

    static byte[] WithSize(uint width, uint height)
    {
        byte[] bytes = [.. Png];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    static Task<bool> Refresh(HttpStatusCode status, byte[] body, string file) =>
        ModRepository.RefreshIconAsync(new HttpClient(new Answer(status, body)), ModSource.Official, Listing, file);

    [Fact]
    public void Only_a_png_of_a_sensible_size_is_an_icon()
    {
        Assert.True(ModRepository.IsIcon(Png));
        Assert.True(ModRepository.IsIcon(WithSize(1024, 1024)));
        Assert.False(ModRepository.IsIcon(WithSize(1025, 1)));
        Assert.False(ModRepository.IsIcon(WithSize(1, 100_000)));
        Assert.False(ModRepository.IsIcon(WithSize(0, 1)));
        Assert.False(ModRepository.IsIcon(Png.AsSpan(0, 20)));
        Assert.False(ModRepository.IsIcon("<html>not found</html>                 "u8));
        Assert.False(ModRepository.IsIcon([.. Png, .. new byte[1024 * 1024]]));
    }

    [Fact]
    public async Task A_repository_icon_is_kept_until_the_repository_stops_having_it()
    {
        string file = Path.Combine(_root, "kept", "alpha.png");
        var answer = new Answer(HttpStatusCode.OK, Png);
        Assert.True(await ModRepository.RefreshIconAsync(new HttpClient(answer), ModSource.Official, Listing, file));
        Assert.Equal("https://raw.githubusercontent.com/LocalExile/LocalExile-Mods/main/mods/alpha/icon.png", answer.Asked?.AbsoluteUri);
        Assert.Equal(Png, File.ReadAllBytes(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!));

        Assert.False(await Refresh(HttpStatusCode.OK, Png, file));

        // A failed read says nothing about the icon: it stays.
        await Assert.ThrowsAsync<HttpRequestException>(() => Refresh(HttpStatusCode.InternalServerError, [], file));
        Assert.True(File.Exists(file));

        Assert.True(await Refresh(HttpStatusCode.NotFound, [], file));
        Assert.False(File.Exists(file));
        Assert.False(await Refresh(HttpStatusCode.NotFound, [], file));

        // Something that is no icon, or too large to be one, is not written.
        Assert.False(await Refresh(HttpStatusCode.OK, "not a png"u8.ToArray(), file));
        Assert.False(await Refresh(HttpStatusCode.OK, [.. Png, .. new byte[2 * 1024 * 1024]], file));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_mod_shows_its_own_icon_then_its_repositorys_then_its_symbol()
    {
        Directory.CreateDirectory(_root);
        string own = Path.Combine(_root, "own.png"), damaged = Path.Combine(_root, "damaged.png");
        File.WriteAllBytes(own, Png);
        File.WriteAllBytes(damaged, [.. Png.AsSpan(0, 24), 1, 2, 3]);
        var entry = new ModEntry("alpha", "1.0.0", "feature", [], true, "Not checked", "", "", "", "hash");
        var item = new BrowseItem(ModSource.Official, Listing, null, entry);

        // Nothing of its own, and nothing kept from the repository: the symbol, wherever it is drawn.
        var bare = new InstalledRow(entry, null);
        Assert.Null(bare.IconImage); Assert.True(bare.ShowSymbol);
        Assert.Equal(ModRepository.IconFile(ModSource.Official, "alpha"), new InstalledRow(entry, item).IconFile);

        var drawn = new InstalledRow(entry with { IconFile = own }, item);
        Assert.Equal(own, drawn.IconFile);
        Assert.NotNull(drawn.IconImage); Assert.False(drawn.ShowSymbol);
        Assert.Equal(own, (item with { Installed = entry with { IconFile = own } }).IconFile);

        Assert.True(new InstalledRow(entry with { IconFile = damaged }, null).ShowSymbol);
        Assert.True(new InstalledRow(entry with { IconFile = Path.Combine(_root, "gone.png") }, null).ShowSymbol);

        // Replacing the file is seen without restarting.
        File.WriteAllBytes(own, "broken"u8.ToArray());
        File.SetLastWriteTimeUtc(own, DateTime.UtcNow.AddHours(1));
        Assert.True(drawn.ShowSymbol);
    }

    [Fact]
    public void The_catalog_finds_an_icon_beside_the_mod_or_in_its_unbuilt_project()
    {
        string binaries = Path.Combine(_root, "Mods", "Binaries"), plain = Path.Combine(binaries, "plain"), iconic = Path.Combine(binaries, "iconic"),
            project = Path.Combine(_root, "Mods", "Source", "Unbuilt");
        Directory.CreateDirectory(plain); Directory.CreateDirectory(iconic); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(plain, "Plain.cs"), "");
        File.WriteAllText(Path.Combine(iconic, "Iconic.cs"), "");
        File.WriteAllBytes(Path.Combine(iconic, ModRepository.IconName), Png);
        File.WriteAllText(Path.Combine(project, "Unbuilt.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(project, "mod.json"), """{ "id": "unbuilt", "version": "1.0.0" }""");
        File.WriteAllBytes(Path.Combine(project, ModRepository.IconName), Png);

        var mods = ModCatalog.Read(new RuntimeSnapshot(_root, "", "", "", "", "", [], [], [], ModEnvironment: new(true, binaries, true))).ToDictionary(e => e.Id);

        Assert.Equal("", mods["plain"].IconFile);
        Assert.Equal(Path.Combine(iconic, ModRepository.IconName), mods["iconic"].IconFile);
        Assert.Equal(Path.Combine(project, ModRepository.IconName), mods["unbuilt"].IconFile);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
