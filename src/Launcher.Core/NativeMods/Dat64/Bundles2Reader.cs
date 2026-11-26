using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using OozSharp;
using OozSharp.Exceptions;

namespace LocalExile.Client.AssetPipeline.Dat64;

/// <summary>
/// Read-only Path of Exile 3.29 Steam <c>Bundles2/</c> file reader.
/// Uses pure-managed <see cref="Kraken"/> (OozSharp) for Kraken/Mermaid chunks and automatically falls back
/// to LocalExile's persistent <c>ooz_worker.mjs</c> (WASM ooz) for Selkie/Leviathan chunks when present.
/// </summary>
public sealed class Bundles2Reader : IDisposable
{
    private const int FileRecordSize = 20;
    private readonly string _clientRoot;
    private readonly string[] _bundleFiles;
    private readonly Dictionary<ulong, (int BundleIndex, int Offset, int Size)> _index;
    private readonly HybridOodleDecoder _decoder;

    private Bundles2Reader(
        string clientRoot,
        string[] bundleFiles,
        Dictionary<ulong, (int BundleIndex, int Offset, int Size)> index,
        HybridOodleDecoder decoder)
    {
        _clientRoot = clientRoot;
        _bundleFiles = bundleFiles;
        _index = index;
        _decoder = decoder;
    }

    public int FileCount => _index.Count;

    public static Bundles2Reader Open(string clientDirectory, string? oozWorkerScript = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientDirectory);
        string indexPath = Path.Combine(clientDirectory, "Bundles2", "_.index.bin");
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException($"PoE Bundles2 index not found at '{indexPath}'.", indexPath);
        }

        string? resolvedWorker = oozWorkerScript ?? DiscoverOozWorker(clientDirectory);
        var decoder = new HybridOodleDecoder(resolvedWorker);
        try
        {
            byte[] indexBundle = File.ReadAllBytes(indexPath);
            byte[] indexContent = DecompressWhole(indexBundle, decoder);

            int offset = 0;
            int bundleCount = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset, 4));
            offset += 4;

            string[] bundleFiles = new string[bundleCount];
            for (int i = 0; i < bundleCount; i++)
            {
                int nameLen = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset, 4));
                offset += 4;
                string name = Encoding.UTF8.GetString(indexContent, offset, nameLen);
                offset += nameLen;
                offset += 4; // uncompressed size

                if (!name.EndsWith(".bundle.bin", StringComparison.OrdinalIgnoreCase))
                {
                    name += ".bundle.bin";
                }

                if (!name.StartsWith("Bundles2/", StringComparison.OrdinalIgnoreCase))
                {
                    name = "Bundles2/" + name;
                }

                bundleFiles[i] = name;
            }

            int fileCount = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset, 4));
            offset += 4;

            var map = new Dictionary<ulong, (int, int, int)>(fileCount);
            for (int i = 0; i < fileCount; i++)
            {
                ReadOnlySpan<byte> rec = indexContent.AsSpan(offset + (i * FileRecordSize), FileRecordSize);
                ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(rec[..8]);
                int bundleIdx = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(8, 4));
                int fileOffset = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(12, 4));
                int size = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(16, 4));
                map.TryAdd(hash, (bundleIdx, fileOffset, size));
            }

            return new Bundles2Reader(clientDirectory, bundleFiles, map, decoder);
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    public bool TryReadFile(string virtualPath, out byte[] data)
    {
        ulong hash = PoeHashing.HashBundlePath(virtualPath);
        if (!_index.TryGetValue(hash, out var loc) || (uint)loc.BundleIndex >= (uint)_bundleFiles.Length)
        {
            data = [];
            return false;
        }

        string bundlePath = Path.Combine(_clientRoot, _bundleFiles[loc.BundleIndex].Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(bundlePath))
        {
            data = [];
            return false;
        }

        byte[] bundle = File.ReadAllBytes(bundlePath);
        data = DecompressSlice(bundle, loc.Offset, loc.Size, _decoder);
        return true;
    }

    public byte[] ReadFile(string virtualPath)
    {
        if (!TryReadFile(virtualPath, out byte[] data))
        {
            throw new FileNotFoundException($"File '{virtualPath}' not found in PoE Bundles2 index.", virtualPath);
        }

        return data;
    }

    public void Dispose() => _decoder.Dispose();

    private static string? DiscoverOozWorker(string clientDirectory)
    {
        string full = Path.GetFullPath(clientDirectory);
        string? parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent))
        {
            string candidate = Path.Combine(parent, "Tools", "ooz", "ooz_worker.mjs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static byte[] DecompressWhole(ReadOnlySpan<byte> bundle, HybridOodleDecoder decoder)
    {
        int decompressedSize = BinaryPrimitives.ReadInt32LittleEndian(bundle[..4]);
        return DecompressSlice(bundle, 0, decompressedSize, decoder);
    }

    private static byte[] DecompressSlice(ReadOnlySpan<byte> bundle, int sliceOffset, int sliceSize, HybridOodleDecoder decoder)
    {
        int decompressedSize = BinaryPrimitives.ReadInt32LittleEndian(bundle[..4]);
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(bundle.Slice(36, 4));
        int granularity = BinaryPrimitives.ReadInt32LittleEndian(bundle.Slice(40, 4));

        byte[] result = new byte[sliceSize];
        int chunkBegin = 60 + (chunkCount * 4);
        int decompOffset = 0;
        int destOffset = 0;

        for (int idx = 0; idx < chunkCount && decompOffset < sliceOffset + sliceSize; idx++)
        {
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bundle.Slice(60 + (idx * 4), 4));
            int decompChunkSize = idx == chunkCount - 1 ? decompressedSize - decompOffset : granularity;

            if (decompChunkSize > 0 &&
                Math.Max(decompOffset, sliceOffset) < Math.Min(decompOffset + decompChunkSize, sliceOffset + sliceSize))
            {
                int begin = Math.Max(sliceOffset - decompOffset, 0);
                int end = Math.Min(sliceOffset + sliceSize - decompOffset, decompChunkSize);

                ReadOnlySpan<byte> chunkCompressed = bundle.Slice(chunkBegin, chunkSize);
                byte[] chunkData = chunkSize == decompChunkSize
                    ? chunkCompressed.ToArray()
                    : decoder.Decompress(chunkCompressed, decompChunkSize);

                chunkData.AsSpan(begin, end - begin).CopyTo(result.AsSpan(destOffset));
                destOffset += end - begin;
            }

            decompOffset += decompChunkSize;
            chunkBegin += chunkSize;
        }

        if (destOffset != sliceSize)
        {
            throw new InvalidDataException($"Bundle slice incomplete: wrote {destOffset} of {sliceSize} bytes.");
        }

        return result;
    }

    private sealed class HybridOodleDecoder(string? oozWorkerScript) : IDisposable
    {
        private readonly Kraken _kraken = new();
        private Process? _nodeProcess;
        private Stream? _toWorker;
        private Stream? _fromWorker;
        private bool _preferWorker;

        public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedSize)
        {
            if (!_preferWorker)
            {
                try
                {
                    return _kraken.Decompress(compressed, uncompressedSize).ToArray();
                }
                catch (DecoderException) when (!string.IsNullOrEmpty(oozWorkerScript))
                {
                    _preferWorker = true;
                }
            }

            return DecompressWithNodeWorker(compressed, uncompressedSize);
        }

        private byte[] DecompressWithNodeWorker(ReadOnlySpan<byte> compressed, int uncompressedSize)
        {
            EnsureWorkerStarted();

            Span<byte> reqHeader = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(reqHeader[..4], (uint)uncompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(reqHeader[4..8], (uint)compressed.Length);
            _toWorker!.Write(reqHeader);
            _toWorker.Write(compressed);
            _toWorker.Flush();

            Span<byte> respHeader = stackalloc byte[8];
            ReadExactly(_fromWorker!, respHeader);
            uint status = BinaryPrimitives.ReadUInt32LittleEndian(respHeader[..4]);
            int outLen = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(respHeader[4..8]));

            byte[] payload = new byte[outLen];
            ReadExactly(_fromWorker!, payload);
            if (status != 0)
            {
                throw new InvalidDataException($"ooz_worker error: {Encoding.UTF8.GetString(payload)}");
            }

            return payload;
        }

        private void EnsureWorkerStarted()
        {
            if (_nodeProcess is not null)
            {
                return;
            }

            if (string.IsNullOrEmpty(oozWorkerScript) || !File.Exists(oozWorkerScript))
            {
                throw new InvalidOperationException(
                    "Oodle chunk requires Selkie/Leviathan support; supply pre-extracted Data/*.datc64 or LocalExile Tools/ooz/ooz_worker.mjs.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(oozWorkerScript);

            _nodeProcess = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start node ooz_worker.");
            _toWorker = _nodeProcess.StandardInput.BaseStream;
            _fromWorker = _nodeProcess.StandardOutput.BaseStream;
        }

        private static void ReadExactly(Stream stream, Span<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int n = stream.Read(buffer[offset..]);
                if (n == 0)
                {
                    throw new EndOfStreamException("ooz_worker closed stdout unexpectedly.");
                }

                offset += n;
            }
        }

        public void Dispose()
        {
            if (_nodeProcess is null)
            {
                return;
            }

            try
            {
                _toWorker?.Dispose();
            }
            catch (IOException)
            {
            }

            try
            {
                if (!_nodeProcess.WaitForExit(2000))
                {
                    _nodeProcess.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _nodeProcess.Dispose();
            _nodeProcess = null;
            _toWorker = null;
            _fromWorker = null;
        }
    }
}
