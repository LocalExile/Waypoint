using System.Security.Cryptography;

namespace Launcher.Core.LootFilter;

/// <summary>A file the editor downloads once, from one commit, and checks by hash before using.</summary>
public sealed record FilterSource(string Repository, string Commit, string Path, string Sha256)
{
    public string Url => $"https://raw.githubusercontent.com/{Repository}/{Commit}/{Uri.EscapeDataString(Path).Replace("%2F", "/")}";
}

/// <summary>
/// The base filter and the option file: the last releases made for 3.29, the game LocalExile serves, taken once as the
/// starting point (NeverSink's filter 8.20.2, NeverSinkDev/NeverSink-Filter, MIT; 8.21 is for 3.30 and names items 3.29
/// does not have; FilterBlade's option file as of the same league, NeverSinkDev/FilterBlade-Public-Assets, MIT). Later
/// releases follow the live game and are not taken; a change to the base filter is checked against the client's tables.
/// </summary>
public static class FilterSources
{
    const string NeverSink = "NeverSinkDev/NeverSink-Filter", NeverSinkCommit = "3ab00dd016b00c6be7acfa989baf498842973d54";
    const string Assets = "NeverSinkDev/FilterBlade-Public-Assets", AssetsCommit = "28135b6eb985547416855785cb236954876dc916";

    public const string Version = "8.20.2";

    public static readonly FilterSource Options = new(Assets, AssetsCommit, "FbPoe1Configs/CustomizerDefault.options",
        "2236366ce16b1ba06a06a205dcfb9a9febdea0c580e5e5a38b841f107e804ef8");

    /// <summary>Every base type with its class, size and defences, for the editor's sample drops.</summary>
    public static readonly FilterSource BaseTypes = new(Assets, AssetsCommit, "FbPoe1Configs/BaseTypes.csv",
        "f87b41c9de0dfb97a072b9e6d9d2903bcfafa55c6a2524facca4011c5995bdbf");

    /// <summary>NeverSink's strictness levels, softest first.</summary>
    public static readonly IReadOnlyList<(string Id, FilterSource Source)> Strictness =
    [
        Base("0-SOFT", "a31eefb7ec26ab80bafa5e3a8c3157cb6eb5ba368316ab27808d772e1d45ab21"),
        Base("1-REGULAR", "1952c4edb4c80eedcbb143a03071053fbc7d2d0b62d2a700c888af52a8bee345"),
        Base("2-SEMI-STRICT", "8ac2c2541676037e9e32e305802cc85aece2112bdfeb1f472b17e782cc483c3b"),
        Base("3-STRICT", "a4c41f30d9ebfc92404b24780c9961a590e4f2810bc294a06f8d50797cf0bafa"),
        Base("4-VERY-STRICT", "489c95b412eefdd7587a0ffc5b0ded1e84aeb0beb12ca3f438b3b8485ff51830"),
        Base("5-UBER-STRICT", "56e592923157c1d251c64d80d9ba4024375ab048daff95732134c59f4c8bb717"),
        Base("6-UBER-PLUS-STRICT", "0b10a1c4a5f44ee8a76d5e49befdb50369cefc1b9540101e512c292bafb96d8b"),
    ];

    static (string, FilterSource) Base(string id, string sha) => (id, new FilterSource(NeverSink, NeverSinkCommit, $"NeverSink's filter - {id}.filter", sha));

    public static FilterSource For(string strictness) =>
        Strictness.FirstOrDefault(s => s.Id == strictness).Source ?? throw new ArgumentException($"Unknown strictness {strictness}");

    /// <summary>Where downloaded sources are kept: Waypoint's own data, never the checkout.</summary>
    public static string CacheFolder => System.IO.Path.Combine(Platform.LocalData, "Waypoint", "loot-filter");

    /// <summary>The source's text: from the cache when its hash still matches, else downloaded and checked.</summary>
    public static async Task<string> ReadAsync(HttpClient http, FilterSource source, string? cache = null, CancellationToken stop = default)
    {
        string file = System.IO.Path.Combine(cache ?? CacheFolder, source.Commit[..12], System.IO.Path.GetFileName(source.Path));
        if (File.Exists(file))
        {
            byte[] cached = await File.ReadAllBytesAsync(file, stop);
            if (Hash(cached) == source.Sha256) return Text(cached);
        }
        byte[] bytes = await http.GetByteArrayAsync(source.Url, stop);
        if (Hash(bytes) != source.Sha256)
            throw new InvalidDataException($"{source.Path} from {source.Repository} does not match its pinned hash.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, bytes, stop);
        return Text(bytes);
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static string Text(byte[] bytes) => new System.Text.UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
}
