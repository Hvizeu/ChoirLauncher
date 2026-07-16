using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChoirLauncher.Core;

public sealed class LauncherSettingsDocument
{
    private static readonly Regex ModsBlock = new(
        @"(?ms)^(?<indent>[ \t]*)MODS\s*:\s*\[(?<body>.*?)^[ \t]*\]\s*,?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Quoted = new(
        "\\\"(?<value>(?:\\\\.|[^\\\"])*)\\\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private LauncherSettingsDocument(string originalText, IReadOnlyList<string> enabledMods)
    {
        OriginalText = originalText;
        EnabledMods = enabledMods;
    }

    public string OriginalText { get; }
    public IReadOnlyList<string> EnabledMods { get; }

    public static LauncherSettingsDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = ModsBlock.Match(text);
        if (!match.Success)
            throw new FormatException("Launcher settings has no well-formed MODS array.");
        var values = Quoted.Matches(match.Groups["body"].Value)
            .Select(m => Regex.Unescape(m.Groups["value"].Value))
            .ToArray();
        return new LauncherSettingsDocument(text, values);
    }

    public string WithEnabledMods(IReadOnlyList<string> orderedMods)
    {
        ArgumentNullException.ThrowIfNull(orderedMods);
        foreach (var mod in orderedMods)
        {
            if (string.IsNullOrWhiteSpace(mod) || mod.IndexOfAny(['\r', '\n', '"']) >= 0)
                throw new ArgumentException("Mod references must be non-empty single-line strings without quotes.", nameof(orderedMods));
        }

        var match = ModsBlock.Match(OriginalText);
        var indent = match.Groups["indent"].Value;
        var newline = OriginalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var builder = new StringBuilder();
        builder.Append(indent).Append("MODS: [").Append(newline);
        foreach (var mod in orderedMods)
            builder.Append(indent).Append('\t').Append('"').Append(mod.Replace("\\", "\\\\", StringComparison.Ordinal)).Append("\",").Append(newline);
        builder.Append(indent).Append("],");
        return OriginalText[..match.Index] + builder + OriginalText[(match.Index + match.Length)..];
    }

    public int ReadInt(string key)
    {
        var token = ReadScalarToken(key);
        if (!int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"Launcher setting {key} is not an integer.");
        return value;
    }

    public string ReadString(string key)
    {
        var token = ReadScalarToken(key);
        try
        {
            return JsonSerializer.Deserialize<string>(token) ?? string.Empty;
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Launcher setting {key} is not a string.", ex);
        }
    }

    public string WithScalarValues(IReadOnlyDictionary<string, string> serializedValues)
    {
        ArgumentNullException.ThrowIfNull(serializedValues);
        var result = OriginalText;
        foreach (var pair in serializedValues.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            ValidateKey(pair.Key);
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Value.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException($"Serialized launcher value for {pair.Key} must be a non-empty single-line token.", nameof(serializedValues));
            var regex = Scalar(pair.Key);
            var matches = regex.Matches(result);
            if (matches.Count != 1) throw new FormatException($"Launcher settings must contain exactly one {pair.Key} scalar; found {matches.Count}.");
            result = regex.Replace(result, match => match.Groups["prefix"].Value + pair.Value + match.Groups["suffix"].Value, 1);
        }
        return result;
    }

    public string ContentExcludingScalarValuesSha256(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var canonical = OriginalText;
        foreach (var key in keys.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            ValidateKey(key);
            var regex = Scalar(key);
            var matches = regex.Matches(canonical);
            if (matches.Count != 1) throw new FormatException($"Launcher settings must contain exactly one {key} scalar; found {matches.Count}.");
            canonical = regex.Replace(canonical, match => match.Groups["prefix"].Value + $"<{key}>" + match.Groups["suffix"].Value, 1);
        }
        return Sha256(canonical);
    }

    private string ReadScalarToken(string key)
    {
        ValidateKey(key);
        var matches = Scalar(key).Matches(OriginalText);
        if (matches.Count != 1) throw new FormatException($"Launcher settings must contain exactly one {key} scalar; found {matches.Count}.");
        return matches[0].Groups["value"].Value;
    }

    private static Regex Scalar(string key) => new(
        "(?m)^(?<prefix>[ \\t]*" + Regex.Escape(key) + "[ \\t]*:[ \\t]*)(?<value>-?[0-9]+|\"(?:\\\\.|[^\"\\\\])*\")(?<suffix>[ \\t]*,?[ \\t]*\\r?$)",
        RegexOptions.CultureInvariant);

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(ch => !(char.IsAsciiLetterUpper(ch) || char.IsDigit(ch) || ch == '_')))
            throw new ArgumentException("Launcher scalar keys must use uppercase ASCII letters, digits, or underscores.", nameof(key));
    }

    public static string Sha256(string text) => Hashing.Sha256(Encoding.UTF8.GetBytes(text));

    public string UnrelatedContentSha256()
    {
        var match = ModsBlock.Match(OriginalText);
        if (!match.Success) throw new FormatException("Launcher settings has no well-formed MODS array.");
        var canonical = OriginalText[..match.Index] + "<MODS>" + OriginalText[(match.Index + match.Length)..];
        return Sha256(canonical);
    }
}
