// PortraitService — stub for OmegaDev's UPK/BCn portrait resolver. Real
// implementation pulls in BCnEncoder + UpkManager + K4os LZ4 native binaries
// and requires access to the game client's cooked/*.upk files. On this build
// we surface the same API shape so Builder + callers compile and run.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OmegaDev2.Services;

public static class PortraitService
{
    public enum MissReason { Unknown, IndexNotBuilt, NotInIndex, DecodeFailed, NoKey, NoInlineMip }

    public static bool IndexBuilt => false;
    public static int  IndexCount => 0;
    public static string LastDiagnostic => "PortraitService stub — no UPK index on this build.";

    // OmegaDev calls this as a delegate: `OnIndexProgress?.Invoke(...)` and also
    // subscribes with `+=`, so it's a field-style Action delegate, not an event.
    public static Action<int, int, string>? OnIndexProgress;

    public static MissReason? GetLastMissReason(string iconAssetPath) => MissReason.IndexNotBuilt;
    public static List<string> GetCandidates(string iconAssetPath) => new();

    public static Task BuildIndexAsync(string cookedDir, Action<string>? log = null) => Task.CompletedTask;
    public static Task BuildIndexAsync(IEnumerable<string> cookedDirs, Action<string>? log = null) => Task.CompletedTask;

    // Pixels stub — Builder consumes .Width/.Height/.Rgba fields.
    public sealed class Pixels
    {
        public int Width;
        public int Height;
        public byte[] Rgba = Array.Empty<byte>();
        public byte[] Bgra = Array.Empty<byte>();
    }
    public static Task<Pixels?> ResolveAsync(string iconAssetPath, CancellationToken ct = default)
        => Task.FromResult<Pixels?>(null);

    public static Task<byte[]?> GetPortraitPngAsync(string protoRefOrPath) => Task.FromResult<byte[]?>(null);
    public static Task<byte[]?> GetPortraitPngAsync(string protoRefOrPath, int size) => Task.FromResult<byte[]?>(null);
    public static Task<byte[]?> GetIconPngAsync(string iconAssetPath) => Task.FromResult<byte[]?>(null);
    public static Task<byte[]?> GetIconPngAsync(string iconAssetPath, int size) => Task.FromResult<byte[]?>(null);
}
