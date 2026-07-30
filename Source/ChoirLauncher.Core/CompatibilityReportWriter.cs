using System.Net;
using System.Text;
using System.Text.Json;

namespace ChoirLauncher.Core;

public enum CompatibilityReportFormat
{
    Json,
    Markdown,
    Html
}

public static class CompatibilityReportWriter
{
    public static string Write(string targetPath, ScanReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(report);

        var format = FormatFromPath(targetPath);
        var text = format switch
        {
            CompatibilityReportFormat.Json => ProfileStore.ExportRedactedScan(report),
            CompatibilityReportFormat.Markdown => RenderMarkdown(report),
            _ => RenderHtml(report)
        };
        var bytes = new UTF8Encoding(false).GetBytes(text);
        AtomicFile.WriteValidated(Path.GetFullPath(targetPath), bytes, candidate => IsValid(candidate, format), null, 0);
        return Hashing.Sha256(bytes);
    }

    public static CompatibilityReportFormat FormatFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => CompatibilityReportFormat.Json,
            ".md" or ".markdown" => CompatibilityReportFormat.Markdown,
            ".html" or ".htm" => CompatibilityReportFormat.Html,
            _ => throw new ArgumentException("Compatibility report must use .html, .md, or .json.", nameof(path))
        };

    public static string RenderMarkdown(ScanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ChoirLauncher Compatibility Report").AppendLine()
            .AppendLine($"- Target game: {Md(report.TargetGameVersion)}")
            .AppendLine($"- Scanned: {report.ScannedAtUtc:O}")
            .AppendLine($"- Installations: {report.Mods.Count}")
            .AppendLine($"- Enabled: {report.Mods.Count(x => x.Enabled)}")
            .AppendLine($"- Findings: {report.Conflicts.Count}")
            .AppendLine($"- Vanilla comparison: {VanillaStatus(report)}")
            .AppendLine()
            .AppendLine("## Summary")
            .AppendLine()
            .AppendLine("| Severity | Count |")
            .AppendLine("|---|---:|");
        foreach (var severity in Enum.GetValues<Severity>())
            builder.AppendLine($"| {severity} | {report.Conflicts.Count(x => x.Severity == severity)} |");

        builder.AppendLine().AppendLine("## Enabled order").AppendLine();
        if (report.EnabledOrder.Count == 0) builder.AppendLine("_No enabled mods._");
        else
            for (var index = 0; index < report.EnabledOrder.Count; index++)
                builder.AppendLine($"{index + 1}. {Md(report.EnabledOrder[index])}");

        builder.AppendLine().AppendLine("## Findings").AppendLine();
        if (report.Conflicts.Count == 0) builder.AppendLine("_No static findings._");
        foreach (var finding in report.Conflicts)
        {
            builder.AppendLine($"### {finding.Severity}: {Md(finding.Category)}")
                .AppendLine()
                .AppendLine($"- Target: {Md(finding.Target)}")
                .AppendLine($"- Confidence: {finding.Confidence}")
                .AppendLine($"- Mods: {Md(string.Join(", ", finding.InvolvedMods))}")
                .AppendLine($"- Explanation: {Md(finding.Explanation)}")
                .AppendLine($"- Recommended action: {Md(finding.RecommendedAction)}");
            if (finding.Evidence.Count > 0)
            {
                builder.AppendLine("- Evidence:");
                foreach (var evidence in finding.Evidence) builder.AppendLine($"  - {Md(evidence)}");
            }
            builder.AppendLine();
        }

        if (report.VanillaComparison.Diagnostics.Count > 0)
        {
            builder.AppendLine("## Scanner diagnostics").AppendLine();
            foreach (var diagnostic in report.VanillaComparison.Diagnostics) builder.AppendLine($"- {Md(diagnostic)}");
        }
        builder.AppendLine().AppendLine("Static analysis can identify structural risks; it cannot prove gameplay compatibility.");
        return builder.ToString();
    }

    public static string RenderHtml(ScanReport report)
    {
        var rows = string.Join(Environment.NewLine, report.Conflicts.Select(finding =>
            $"<tr class=\"severity-{H(finding.Severity.ToString().ToLowerInvariant())}\"><td>{H(finding.Severity.ToString())}</td>" +
            $"<td>{H(finding.Category)}</td><td>{H(finding.Target)}</td><td>{H(string.Join(", ", finding.InvolvedMods))}</td>" +
            $"<td>{H(finding.Explanation)}<br><strong>Action:</strong> {H(finding.RecommendedAction)}{EvidenceHtml(finding.Evidence)}</td></tr>"));
        if (rows.Length == 0) rows = "<tr><td colspan=\"5\">No static findings.</td></tr>";

        var counts = string.Join("", Enum.GetValues<Severity>().Select(severity =>
            $"<li><strong>{H(severity.ToString())}</strong><span>{report.Conflicts.Count(x => x.Severity == severity)}</span></li>"));
        var enabledOrder = report.EnabledOrder.Count == 0
            ? "<li>No enabled mods.</li>"
            : string.Join("", report.EnabledOrder.Select((mod, index) => $"<li><span>{index + 1}</span>{H(mod)}</li>"));

        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ChoirLauncher Compatibility Report</title>
<style>
:root{color-scheme:dark;background:#15110d;color:#e5d6b7;font:15px system-ui,sans-serif}body{max-width:1500px;margin:auto;padding:28px}
h1,h2{color:#f0dfbd}small,.muted{color:#b4aa97}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;padding:0}
.cards li,.panel{list-style:none;background:#241c15;border:1px solid #6d5234;border-radius:6px;padding:12px}.cards span{float:right;font-size:1.3em}
table{width:100%;border-collapse:collapse;background:#1d1712}th,td{border:1px solid #5c4936;padding:9px;vertical-align:top;text-align:left}
th{background:#302319}.severity-blocking td:first-child{color:#ff6b61}.severity-high td:first-child{color:#ffad6b}.severity-medium td:first-child{color:#f4d66d}
.order{columns:3;column-width:240px}.order li{break-inside:avoid;padding:3px}.order span{display:inline-block;width:34px;color:#a9906c}
code{color:#e6c48f}details{margin-top:8px}
</style>
</head>
<body>
<h1>ChoirLauncher Compatibility Report</h1>
<p class="muted">Target {{H(report.TargetGameVersion)}} · scanned {{H(report.ScannedAtUtc.ToString("O"))}} · {{report.Mods.Count}} installations · {{report.Mods.Count(x => x.Enabled)}} enabled</p>
<p class="panel"><strong>Vanilla comparison:</strong> {{H(VanillaStatus(report))}}</p>
<h2>Summary</h2>
<ul class="cards">{{counts}}</ul>
<h2>Enabled order</h2>
<ol class="panel order">{{enabledOrder}}</ol>
<h2>Findings</h2>
<table><thead><tr><th>Severity</th><th>Category</th><th>Target</th><th>Mods</th><th>Assessment</th></tr></thead><tbody>
{{rows}}
</tbody></table>
<p class="muted">Static analysis can identify structural risks; it cannot prove gameplay compatibility.</p>
</body>
</html>
""";
    }

    private static string VanillaStatus(ScanReport report) => report.VanillaComparison.Available
        ? $"{report.VanillaComparison.ClassCount} game classes and {report.VanillaComparison.DataPathCount} data paths indexed"
        : "unavailable";

    private static string EvidenceHtml(IReadOnlyList<string> evidence) => evidence.Count == 0
        ? ""
        : $"<details><summary>Evidence ({evidence.Count})</summary><ul>{string.Join("", evidence.Select(x => $"<li><code>{H(x)}</code></li>"))}</ul></details>";

    private static bool IsValid(byte[] candidate, CompatibilityReportFormat format)
    {
        try
        {
            var text = Encoding.UTF8.GetString(candidate);
            if (format == CompatibilityReportFormat.Json)
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.ValueKind == JsonValueKind.Object;
            }
            return format == CompatibilityReportFormat.Markdown
                ? text.StartsWith("# ChoirLauncher Compatibility Report", StringComparison.Ordinal)
                : text.Contains("<html lang=\"en\">", StringComparison.Ordinal) &&
                  text.Contains("</html>", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
