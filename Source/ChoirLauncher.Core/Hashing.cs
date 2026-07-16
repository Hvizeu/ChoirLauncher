using System.Security.Cryptography;

namespace ChoirLauncher.Core;

public static class Hashing
{
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256(stream);
    }

    public static string Sha256(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
