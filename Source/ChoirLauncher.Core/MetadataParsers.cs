using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChoirLauncher.Core;

public static class MetadataParsers
{
    private static readonly Regex InfoEntry = new(
        "(?m)^\\s*(?<key>[A-Za-z0-9_]+)\\s*:\\s*(?<value>\\\"(?:\\\\.|[^\\\"])*\\\"|[^,\\r\\n]+)\\s*,?\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ModMetadata ParseInfo(string? text)
    {
        var diagnostics = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return new("", "", "", 0, null, "", "", false, ["Missing _Info.txt."]);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in InfoEntry.Matches(text))
        {
            var raw = match.Groups["value"].Value.Trim();
            if (raw.StartsWith('"') && raw.EndsWith('"'))
            {
                try { raw = JsonSerializer.Deserialize<string>(raw) ?? ""; }
                catch (JsonException) { diagnostics.Add($"Malformed quoted value for {match.Groups["key"].Value}."); }
            }
            values[match.Groups["key"].Value] = raw;
        }

        var major = ParseInt(values, "GAME_VERSION_MAJOR", diagnostics);
        int? minor = values.TryGetValue("GAME_VERSION_MINOR", out var minorText) && int.TryParse(minorText, out var parsedMinor)
            ? parsedMinor : null;
        var name = Get(values, "NAME");
        if (string.IsNullOrWhiteSpace(name)) diagnostics.Add("Missing NAME.");
        if (major <= 0) diagnostics.Add("Missing or invalid GAME_VERSION_MAJOR.");
        return new(name, Get(values, "DESC"), Get(values, "VERSION"), major, minor,
            Get(values, "AUTHOR"), Get(values, "INFO"), diagnostics.Count == 0, diagnostics);
    }

    public static IReadOnlyDictionary<string, string> ParseProperties(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
            var separator = line.IndexOf('=');
            if (separator < 1) separator = line.IndexOf(':');
            if (separator < 1) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    public static ChoirManifest ParseChoirManifest(string text, string kind = "choir/core-platform.properties")
    {
        var values = ParseProperties(text);
        var diagnostics = new List<string>();
        var format = ParseInt(values, "formatVersion", diagnostics);
        var modId = Get(values, "modId");
        var version = Get(values, "version");
        if (!IsStableId(modId)) diagnostics.Add("modId is missing or invalid.");
        if (string.IsNullOrWhiteSpace(version)) diagnostics.Add("version is missing.");
        if (format != 1) diagnostics.Add($"Unsupported manifest formatVersion={format}.");

        return new(format, modId, Get(values, "displayName"), version,
            ParseDependencies(Get(values, "requires"), false, diagnostics),
            ParseDependencies(Get(values, "optional"), true, diagnostics),
            SplitCsv(Get(values, "incompatible")), SplitCsv(Get(values, "capabilities")),
            NullIfEmpty(Get(values, "gameVersion")), NullIfEmpty(Get(values, "choirApi")),
            kind, diagnostics.Count == 0, diagnostics);
    }

    public static ChoirManifest ParseChoirJsonManifest(string text)
    {
        var diagnostics = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var format = GetInt(root, "formatVersion");
            var modId = GetString(root, "modId");
            var version = GetString(root, "version");
            if (!IsStableId(modId)) diagnostics.Add("modId is missing or invalid.");
            if (string.IsNullOrWhiteSpace(version)) diagnostics.Add("version is missing.");
            if (format != 1) diagnostics.Add($"Unsupported manifest formatVersion={format}.");
            return new(format, modId, GetString(root, "displayName"), version,
                JsonDependencies(root, "requires", false, diagnostics),
                JsonDependencies(root, "optional", true, diagnostics),
                JsonStrings(root, "incompatible"), JsonStrings(root, "capabilities"),
                NullIfEmpty(GetString(root, "gameVersion")), NullIfEmpty(GetString(root, "choirApi")),
                "META-INF/choir/mod.json", diagnostics.Count == 0, diagnostics);
        }
        catch (JsonException ex)
        {
            return new(0, "", "", "", [], [], [], [], null, null, "META-INF/choir/mod.json", false,
                [$"Malformed JSON manifest: {ex.Message}"]);
        }
    }

    public static string? ParseOptionsProviderId(string text)
    {
        var values = ParseProperties(text);
        return values.TryGetValue("providerId", out var provider) && IsStableId(provider) ? provider : null;
    }

    public static bool IsStableId(string value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant);

    private static IReadOnlyList<DependencySpec> ParseDependencies(string text, bool optional, List<string> diagnostics)
    {
        var results = new List<DependencySpec>();
        foreach (var item in SplitCsv(text))
        {
            var split = item.Split('@', 2);
            if (!IsStableId(split[0])) { diagnostics.Add($"Invalid dependency ID: {split[0]}"); continue; }
            results.Add(new(split[0], split.Length == 2 ? split[1] : "*", optional));
        }
        return results;
    }

    private static IReadOnlyList<DependencySpec> JsonDependencies(JsonElement root, string name, bool optional, List<string> diagnostics)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array) return [];
        var result = new List<DependencySpec>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.AddRange(ParseDependencies(item.GetString() ?? "", optional, diagnostics));
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(item, "modId");
            if (!IsStableId(id)) { diagnostics.Add($"Invalid dependency ID: {id}"); continue; }
            result.Add(new(id, NullIfEmpty(GetString(item, "version")) ?? "*", optional));
        }
        return result;
    }

    private static IReadOnlyList<string> JsonStrings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : [];

    private static int GetInt(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static IReadOnlyList<string> SplitCsv(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
    private static string Get(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value) ? value : "";
    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key, List<string> diagnostics)
    {
        if (values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)) return parsed;
        diagnostics.Add($"Missing or invalid {key}.");
        return 0;
    }
}
