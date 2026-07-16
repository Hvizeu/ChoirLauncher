using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ChoirLauncher.Core;

public sealed record SongsOfSyxGameVersion(int Major, int Minor)
{
    public string Display => $"0.{Major}.{Minor}";
}

public sealed record SongsOfSyxGameArtifactProbe(
    SongsOfSyxGameVersion? Version,
    string? JarSha256,
    bool StructurallyValid,
    bool KnownBuild,
    string BuildLabel,
    IReadOnlyList<string> Diagnostics);

public static class KnownGameBuildCatalog
{
    public const string V7144JarSha256 = "fe0137c05408e5ee9e31f06fc44fe37a5d4372d9c985b280f0852d1347c0224b";
    public const string V7144DirectExecutableSha256 = "d7e2350ea6191560b2482a31f5053f6f2c48c6de8dab4a27b3c85bc5c14199f5";
    public const string V7144OfficialLauncherSha256 = "8dc43bb4ce518b02bc4c85da62efd767163efff76523cdfcb94ed638b7bfaf9b";

    public static string? IdentifyJar(string sha256) =>
        sha256.Equals(V7144JarSha256, StringComparison.OrdinalIgnoreCase) ? "Songs of Syx 0.71.44 (known artifact)" : null;

    public static string? IdentifyExecutable(GameLaunchRoute route, string sha256) => route switch
    {
        GameLaunchRoute.DirectGame when sha256.Equals(V7144DirectExecutableSha256, StringComparison.OrdinalIgnoreCase) =>
            "Songs of Syx 0.71.44 direct executable (known artifact)",
        GameLaunchRoute.OfficialLauncher when sha256.Equals(V7144OfficialLauncherSha256, StringComparison.OrdinalIgnoreCase) =>
            "Songs of Syx 0.71.44 official launcher (known artifact)",
        _ => null
    };
}

public static class SongsOfSyxGameArtifactInspector
{
    private const int MaxVersionClassBytes = 1024 * 1024;

    public static SongsOfSyxGameArtifactProbe Inspect(string? jarPath)
    {
        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath))
            return new(null, null, false, false, "Game JAR unavailable", ["SongsOfSyx.jar was not found."]);

        string? hash = null;
        try
        {
            if ((File.GetAttributes(jarPath) & FileAttributes.ReparsePoint) != 0)
                return new(null, null, false, false, "Untrusted game JAR path", ["SongsOfSyx.jar may not be a reparse point."]);

            hash = Hashing.Sha256File(jarPath);
            using var archive = ZipFile.OpenRead(jarPath);
            var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
            var mainClass = archive.GetEntry("init/Main.class");
            var versionClass = archive.GetEntry("game/VERSION.class");
            if (manifest is null || (mainClass is null && versionClass is null))
                return new(null, hash, false, false, "Unrecognized JAR structure",
                    ["The selected JAR does not contain the expected Songs of Syx manifest and entry classes."]);

            SongsOfSyxGameVersion? version = null;
            if (versionClass is not null)
            {
                try { version = ReadVersionClass(versionClass); }
                catch (InvalidDataException ex) { diagnostics.Add("Could not read game.VERSION metadata: " + ex.Message); }
            }
            else
            {
                diagnostics.Add("This game build has no game/VERSION.class entry; its version is reported as unknown.");
            }

            var known = KnownGameBuildCatalog.IdentifyJar(hash);
            var label = known ?? (version is null ? "Structurally valid Songs of Syx build" : $"Songs of Syx {version.Display} (unrecognized artifact)");
            if (known is null)
                diagnostics.Add("The game checksum is not in ChoirLauncher's known-build catalog. This is informational and does not block launch.");
            return new(version, hash, true, known is not null, label, diagnostics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add("Could not inspect SongsOfSyx.jar: " + ex.Message);
            return new(null, hash, false, false, "Unreadable game JAR", diagnostics);
        }
    }

    private static SongsOfSyxGameVersion ReadVersionClass(ZipArchiveEntry entry)
    {
        if (entry.Length <= 0 || entry.Length > MaxVersionClassBytes)
            throw new InvalidDataException("game/VERSION.class has an invalid size.");
        using var input = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        input.CopyTo(buffer);
        if (buffer.Length > MaxVersionClassBytes) throw new InvalidDataException("game/VERSION.class exceeds the inspection limit.");
        return JavaVersionClassReader.Read(buffer.ToArray());
    }

    private static class JavaVersionClassReader
    {
        public static SongsOfSyxGameVersion Read(ReadOnlySpan<byte> bytes)
        {
            var reader = new BigEndianReader(bytes);
            if (reader.U4() != 0xCAFEBABE) throw new InvalidDataException("Invalid Java class magic.");
            _ = reader.U2();
            _ = reader.U2();
            var constantPoolCount = reader.U2();
            var utf8 = new string?[constantPoolCount];
            var integers = new int?[constantPoolCount];
            for (var index = 1; index < constantPoolCount; index++)
            {
                var tag = reader.U1();
                switch (tag)
                {
                    case 1:
                        utf8[index] = Encoding.UTF8.GetString(reader.Bytes(reader.U2()));
                        break;
                    case 3:
                        integers[index] = unchecked((int)reader.U4());
                        break;
                    case 4:
                        reader.Skip(4);
                        break;
                    case 5:
                    case 6:
                        reader.Skip(8);
                        index++;
                        break;
                    case 7:
                    case 8:
                    case 16:
                    case 19:
                    case 20:
                        reader.Skip(2);
                        break;
                    case 9:
                    case 10:
                    case 11:
                    case 12:
                    case 17:
                    case 18:
                        reader.Skip(4);
                        break;
                    case 15:
                        reader.Skip(3);
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported Java constant-pool tag {tag}.");
                }
            }

            reader.Skip(6);
            var interfaces = reader.U2();
            reader.Skip(interfaces * 2);
            var fields = reader.U2();
            int? major = null;
            int? minor = null;
            for (var field = 0; field < fields; field++)
            {
                reader.Skip(2);
                var nameIndex = reader.U2();
                reader.Skip(2);
                var attributes = reader.U2();
                var fieldName = Utf8At(utf8, nameIndex);
                for (var attribute = 0; attribute < attributes; attribute++)
                {
                    var attributeName = Utf8At(utf8, reader.U2());
                    var length = reader.U4();
                    if (attributeName == "ConstantValue" && length == 2)
                    {
                        var constantIndex = reader.U2();
                        if (constantIndex >= integers.Length || integers[constantIndex] is not int value)
                            throw new InvalidDataException($"{fieldName} does not reference an integer ConstantValue.");
                        if (fieldName == "VERSION_MAJOR") major = value;
                        if (fieldName == "VERSION_MINOR") minor = value;
                    }
                    else
                    {
                        reader.Skip(checked((int)length));
                    }
                }
            }

            if (major is null || minor is null) throw new InvalidDataException("VERSION_MAJOR or VERSION_MINOR is missing.");
            if (major <= 0 || major > ushort.MaxValue || minor < 0 || minor > ushort.MaxValue)
                throw new InvalidDataException("The embedded game version is outside the supported numeric range.");
            return new(major.Value, minor.Value);
        }

        private static string Utf8At(string?[] values, int index) =>
            index > 0 && index < values.Length && values[index] is { } value
                ? value
                : throw new InvalidDataException("Invalid Java UTF-8 constant-pool reference.");
    }

    private ref struct BigEndianReader
    {
        private readonly ReadOnlySpan<byte> bytes;
        private int offset;

        public BigEndianReader(ReadOnlySpan<byte> bytes)
        {
            this.bytes = bytes;
            offset = 0;
        }

        public byte U1()
        {
            Ensure(1);
            return bytes[offset++];
        }

        public ushort U2()
        {
            Ensure(2);
            var value = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            offset += 2;
            return value;
        }

        public uint U4()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            offset += 4;
            return value;
        }

        public ReadOnlySpan<byte> Bytes(int count)
        {
            Ensure(count);
            var value = bytes.Slice(offset, count);
            offset += count;
            return value;
        }

        public void Skip(int count)
        {
            Ensure(count);
            offset += count;
        }

        private readonly void Ensure(int count)
        {
            if (count < 0 || offset > bytes.Length - count) throw new InvalidDataException("Truncated Java class data.");
        }
    }
}
