using ChoirLauncher.Core;

namespace ChoirLauncher.Desktop;

public sealed class ModRowViewModel : ObservableObject
{
    private bool enabled;
    public required string EntryId { get; init; }
    public required int Position { get; init; }
    public required ManagerProfileEntry Entry { get; init; }
    public required ResolvedProfileEntry Resolution { get; init; }
    public required Severity? HighestSeverity { get; init; }
    public required string DependencyStatus { get; init; }
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public int DisplayPriority => ModPriorityOrder.ToDisplayPriority(Position);
    public string Name => Resolution.Installation?.Metadata.Name is { Length: > 0 } name ? name : Entry.LogicalModId;
    public string LogicalId => Entry.LogicalModId;
    public string Source => Entry.Source.ToString();
    public string Version => Resolution.Installation?.Manifest?.Version ?? Resolution.Installation?.Metadata.Version ?? Entry.ExpectedVersion ?? "?";
    public string Compatibility => Resolution.Installation?.Metadata.GameVersionMajor is 71 ? "V71" : Resolution.Installation is null ? "Unknown" : $"V{Resolution.Installation.Metadata.GameVersionMajor}";
    public string State => Resolution.Status.ToString().ToUpperInvariant();
    public string Declaration => Resolution.Installation?.Manifest is null ? "Legacy" : "Choir";
    public string SeverityText => HighestSeverity?.ToString().ToUpperInvariant() ?? "—";
    public string Author => Resolution.Installation?.Metadata.Author ?? "";
    public string Description => Resolution.Installation?.Metadata.Description ?? "";
    public bool IsWorkshop => Entry.Source == ModSourceType.Workshop;
}
