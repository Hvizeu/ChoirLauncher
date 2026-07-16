using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ChoirLauncher.Core;

public sealed record GameLanguage(string Code, string DisplayName, double Coverage, int SpriteIndex);

public sealed class GameLauncherText
{
    private readonly IReadOnlyDictionary<string, string> values;
    internal GameLauncherText(string languageCode, IReadOnlyDictionary<string, string> values)
    {
        LanguageCode = languageCode;
        this.values = values;
    }
    public string LanguageCode { get; }
    public string Get(string section, string key, string fallback)
        => values.TryGetValue(section + "." + key, out var value) && value.Length > 0 ? value : fallback;
}

public static class GameLanguageCatalog
{
    private static readonly Regex InfoName = new("(?m)^\\s*NAME\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InfoCoverage = new(@"(?m)^\s*COVERAGE\s*:\s*(?<value>[0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Section = new(@"(?ms)^\s*(?<section>launcher\.(?:ScreenMain|ScreenSetting|ScreenInfo))\s*:\s*\{(?<body>.*?)^\s*\}\s*,?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Entry = new("(?m)^\\s*(?<key>[A-Za-z0-9_-]+)\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"\\s*,?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, string> FriendlyNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [""] = "English", ["cs"] = "Czech", ["de"] = "German", ["es-ES"] = "Spanish", ["fr"] = "French",
        ["hu"] = "Hungarian", ["it"] = "Italian", ["ja"] = "Japanese", ["ko"] = "Korean", ["nl"] = "Dutch",
        ["pl"] = "Polish", ["pt-BR"] = "Portuguese (Brazil)", ["ru"] = "Russian", ["tr"] = "Turkish",
        ["uk"] = "Ukrainian", ["zh-CN"] = "Chinese (Simplified)", ["zh-TW"] = "Chinese (Traditional)"
    };

    public static IReadOnlyList<GameLanguage> Discover(string? gameRoot)
    {
        var languages = new List<GameLanguage> { new("", FriendlyNames[""], 1, 0) };
        var locale = gameRoot is null ? null : Path.Combine(gameRoot, "base", "locale.zip");
        if (locale is null || !File.Exists(locale)) return languages;
        using var archive = ZipFile.OpenRead(locale);
        var infos = archive.Entries
            .Where(entry => entry.FullName.StartsWith("langs/", StringComparison.Ordinal) && entry.FullName.EndsWith("/_Info.txt", StringComparison.Ordinal))
            .Select(entry => (Entry: entry, Code: entry.FullName.Split('/')[1]))
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .ToArray();
        foreach (var item in infos)
        {
            using var reader = new StreamReader(item.Entry.Open(), Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            var internalName = Unescape(InfoName.Match(text).Groups["value"].Value);
            var name = FriendlyNames.TryGetValue(item.Code, out var friendly) ? friendly : internalName.Replace('_', ' ');
            var coverageText = InfoCoverage.Match(text).Groups["value"].Value;
            var coverage = double.TryParse(coverageText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            languages.Add(new(item.Code, name, coverage, languages.Count));
        }
        return languages;
    }

    public static GameLauncherText LoadText(string? gameRoot, string languageCode)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var archivePath = gameRoot is null ? null : Path.Combine(gameRoot, "base", languageCode.Length == 0 ? "data.zip" : "locale.zip");
        var entryPath = languageCode.Length == 0 ? "data/assets/text/dictionary/Dic.txt" : $"langs/{languageCode}/assets/text/dictionary/Dic.txt";
        if (archivePath is not null && File.Exists(archivePath))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var entry = archive.GetEntry(entryPath);
            if (entry is not null)
            {
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
                ParseDictionary(reader.ReadToEnd(), values);
            }
        }
        return new(languageCode, values);
    }

    private static void ParseDictionary(string text, IDictionary<string, string> values)
    {
        foreach (Match section in Section.Matches(text))
        {
            var sectionName = section.Groups["section"].Value;
            foreach (Match entry in Entry.Matches(section.Groups["body"].Value))
                values[sectionName + "." + entry.Groups["key"].Value] = Unescape(entry.Groups["value"].Value);
        }
    }

    private static string Unescape(string value) => Regex.Unescape(value);
}
