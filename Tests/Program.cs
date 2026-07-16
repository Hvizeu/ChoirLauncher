using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using ChoirLauncher.Core;
using ChoirLauncher.Desktop;

var startedAt = DateTime.Now;
var suite = new TestSuite();
suite.Add("settings parses ordered mods", () => EqualSeq(new[] { "A", "B" }, Settings("A", "B").EnabledMods));
suite.Add("settings preserves duplicate entries", () => EqualSeq(new[] { "A", "A" }, Settings("A", "A").EnabledMods));
suite.Add("settings rejects missing MODS", () => Throws<FormatException>(() => LauncherSettingsDocument.Parse("VERSION: 1,")));
suite.Add("settings preserves unknown fields", () => True(Settings("A").WithEnabledMods(["B"]).Contains("UNKNOWN: 9", StringComparison.Ordinal)));
suite.Add("settings preserves newline style", () => True(Settings("A").WithEnabledMods(["B"]).Contains("\r\n", StringComparison.Ordinal)));
suite.Add("settings rejects quoted ID", () => Throws<ArgumentException>(() => Settings("A").WithEnabledMods(["bad\"id"])));
suite.Add("settings rejects newline ID", () => Throws<ArgumentException>(() => Settings("A").WithEnabledMods(["bad\nid"])));
suite.Add("settings SHA deterministic", () => Equal(LauncherSettingsDocument.Sha256("x"), LauncherSettingsDocument.Sha256("x")));
suite.Add("launcher options parse verified v71 keys", () => { var options=LauncherGameOptions.From(LauncherSettingsDocument.Parse(RawFullSettings("A")));True(options.LinearFiltering);Equal(GameScreenMode.Windowed,options.ScreenMode);Equal(14,options.WindowHeight); });
suite.Add("launcher options preserve official misspellings", () => { var changed=LauncherGameOptions.From(LauncherSettingsDocument.Parse(RawFullSettings())) with { WindowHeight=12, BorderlessScale=3 };var text=changed.ApplyTo(LauncherSettingsDocument.Parse(RawFullSettings()));True(text.Contains("WIDOW_HEIGHT: 12",StringComparison.Ordinal));True(text.Contains("WIDOW_SCALE: 3",StringComparison.Ordinal)); });
suite.Add("launcher option edit preserves MODS and unknown fields", () => { var doc=LauncherSettingsDocument.Parse(RawFullSettings("A","B"));var options=LauncherGameOptions.From(doc) with { VSync=true };var parsed=LauncherSettingsDocument.Parse(options.ApplyTo(doc));EqualSeq(new[]{"A","B"},parsed.EnabledMods);True(parsed.OriginalText.Contains("UNKNOWN: 9",StringComparison.Ordinal)); });
suite.Add("launcher option protected hash ignores approved values", () => { var doc=LauncherSettingsDocument.Parse(RawFullSettings("A"));var changed=LauncherSettingsDocument.Parse((LauncherGameOptions.From(doc) with { Debug=true }).ApplyTo(doc));Equal(doc.ContentExcludingScalarValuesSha256(LauncherGameOptions.SupportedKeys),changed.ContentExcludingScalarValuesSha256(LauncherGameOptions.SupportedKeys)); });
suite.Add("launcher option protected hash detects mod change", () => { var doc=LauncherSettingsDocument.Parse(RawFullSettings("A"));var changed=LauncherSettingsDocument.Parse(doc.WithEnabledMods(["B"]));False(doc.ContentExcludingScalarValuesSha256(LauncherGameOptions.SupportedKeys)==changed.ContentExcludingScalarValuesSha256(LauncherGameOptions.SupportedKeys)); });
suite.Add("launcher options reject unsupported FPS", () => Throws<ArgumentOutOfRangeException>(() => (LauncherGameOptions.From(LauncherSettingsDocument.Parse(RawFullSettings())) with { FpsCap=55 }).Validate()));
suite.Add("launcher window size presents vanilla percentage", () => Equal(70, LauncherOptionPresentation.WindowPercent(14)));
suite.Add("launcher borderless scale presents vanilla percentage", () => Equal(100, LauncherOptionPresentation.BorderlessScalePercent(0)));
suite.Add("launcher settings preview lists exact change", () => WithDirectory(root => { var path=Path.Combine(root,"LauncherSettings.txt");File.WriteAllText(path,RawFullSettings("A"));var current=LauncherOptionsService.Load(path);var preview=LauncherOptionsService.CreatePreview(path,current with { Shading=false });Equal("Shading",preview.Changes.Single().Key); }));
suite.Add("launcher settings writer applies sandbox values", () => WithLauncherOptionsFixture((path,preview,writer,store) => { var result=writer.ApplyAsync(preview).GetAwaiter().GetResult();True(result.Success);False(LauncherOptionsService.Load(path).VSync);EqualSeq(new[]{"A"},LauncherSettingsDocument.Parse(File.ReadAllText(path)).EnabledMods);Equal("APPLIED_LAUNCHER_SETTINGS",store.List().Single().Metadata.Result); }));
suite.Add("launcher settings writer rejects stale preview", () => WithLauncherOptionsFixture((path,preview,writer,store) => { File.AppendAllText(path,"\r\nFUTURE: 1,");var result=writer.ApplyAsync(preview).GetAwaiter().GetResult();False(result.Success);False(store.List().Any()); }));
suite.Add("language catalog always exposes English", () => { var languages=GameLanguageCatalog.Discover(null);Equal("",languages.Single().Code);Equal(0,languages.Single().SpriteIndex); });
suite.Add("language text missing archive falls back", () => Equal("Settings",GameLanguageCatalog.LoadText(null,"").Get("launcher.ScreenMain","Settings","Settings")));
suite.Add("language catalog ordering drives official flag index", () => WithLocaleArchive(new Dictionary<string,string> { ["langs/de/_Info.txt"]="NAME: \"Deutsch\",\nCOVERAGE: 0.9,", ["langs/cs/_Info.txt"]="NAME: \"Czech\",\nCOVERAGE: 0.8," }, root => EqualSeq(new[] { "", "cs", "de" }, GameLanguageCatalog.Discover(root).Select(x => x.Code))));
suite.Add("language launcher dictionary resolves translated text", () => WithLocaleArchive(new Dictionary<string,string> { ["langs/de/assets/text/dictionary/Dic.txt"]="launcher.ScreenMain: {\nSettings: \"Einstellungen\",\n}," }, root => Equal("Einstellungen",GameLanguageCatalog.LoadText(root,"de").Get("launcher.ScreenMain","Settings","Settings"))));

suite.Add("info parses name", () => Equal("Test", Info().Name));
suite.Add("info parses version", () => Equal("1.2.3", Info().Version));
suite.Add("info parses major", () => Equal(71, Info().GameVersionMajor));
suite.Add("info parses minor", () => Equal(44, Info().GameVersionMinor));
suite.Add("info parses author", () => Equal("Henrique", Info().Author));
suite.Add("info rejects missing name", () => False(MetadataParsers.ParseInfo("GAME_VERSION_MAJOR: 71,").IsValid));
suite.Add("info rejects missing major", () => False(MetadataParsers.ParseInfo("NAME: \"X\",").IsValid));
suite.Add("info missing file invalid", () => False(MetadataParsers.ParseInfo(null).IsValid));

suite.Add("properties parse equals", () => Equal("bar", MetadataParsers.ParseProperties("foo=bar")["foo"]));
suite.Add("properties parse colon", () => Equal("bar", MetadataParsers.ParseProperties("foo: bar")["foo"]));
suite.Add("properties ignore comment", () => False(MetadataParsers.ParseProperties("#x=y").ContainsKey("x")));
suite.Add("manifest parses ID", () => Equal("fixture.alpha", Manifest().ModId));
suite.Add("manifest parses capability", () => Equal("cap.one", Manifest().Capabilities.Single()));
suite.Add("manifest parses dependency", () => Equal("choir.framework", Manifest().Required.Single().ModId));
suite.Add("manifest parses constraint", () => Equal(">=0.2.0", Manifest().Required.Single().Constraint));
suite.Add("manifest rejects invalid ID", () => False(MetadataParsers.ParseChoirManifest("formatVersion=1\nmodId=bad id\nversion=1.0.0").IsValid));
suite.Add("manifest JSON supported statically", () => Equal("json.fixture", MetadataParsers.ParseChoirJsonManifest("{\"formatVersion\":1,\"modId\":\"json.fixture\",\"version\":\"1.0.0\"}").ModId));
suite.Add("manifest malformed JSON isolated", () => False(MetadataParsers.ParseChoirJsonManifest("{").IsValid));
suite.Add("manifest unknown schema rejected", () => False(MetadataParsers.ParseChoirManifest("formatVersion=99\nmodId=test\nversion=1.0.0").IsValid));
suite.Add("options provider parsed", () => Equal("expandedproduction", MetadataParsers.ParseOptionsProviderId("providerId=expandedproduction")));
suite.Add("options provider invalid rejected", () => Null(MetadataParsers.ParseOptionsProviderId("providerId=bad id")));

suite.Add("version exact", () => True(VersionConstraint.Satisfies("0.3.0", "=0.3.0")));
suite.Add("version greater minimum", () => True(VersionConstraint.Satisfies("0.3.0", ">=0.2.0")));
suite.Add("version fails minimum", () => False(VersionConstraint.Satisfies("0.1.0", ">=0.2.0")));
suite.Add("version less maximum", () => True(VersionConstraint.Satisfies("0.3.0", "<1.0.0")));
suite.Add("version wildcard", () => True(VersionConstraint.Satisfies("garbage", "*")));
suite.Add("version malformed false", () => False(VersionConstraint.Satisfies("garbage", ">=0.2.0")));

suite.Add("graph dependency first", () => EqualSeq(new[] { "choir.framework", "fixture.alpha" }, Graph(Mod("fixture.alpha", requires: [new("choir.framework", ">=0.2.0", false)]), Mod("choir.framework", version: "0.3.0")).DeterministicOrder));
suite.Add("graph missing dependency blocked", () => True(Graph(Mod("fixture", requires: [new("missing", "*", false)])).Blockers.ContainsKey("fixture")));
suite.Add("graph version mismatch blocked", () => True(Graph(Mod("fixture", requires: [new("choir.framework", ">=1.0.0", false)]), Mod("choir.framework", version: "0.3.0")).Blockers.ContainsKey("fixture")));
suite.Add("graph optional missing allowed", () => False(Graph(Mod("fixture", optional: [new("missing", "*", true)])).Blockers.ContainsKey("fixture")));
suite.Add("graph incompatible blocked", () => True(Graph(Mod("a", incompatible: ["b"]), Mod("b")).Blockers.ContainsKey("a")));
suite.Add("graph cycle detected", () => Equal(1, Graph(Mod("a", requires: [new("b", "*", false)]), Mod("b", requires: [new("a", "*", false)])).Cycles.Count));
suite.Add("graph deterministic tie by priority", () => EqualSeq(new[] { "b", "a" }, Graph(Mod("a", priority: 1), Mod("b", priority: 0)).DeterministicOrder));
suite.Add("graph deterministic tie by ID", () => EqualSeq(new[] { "a", "b" }, Graph(Mod("b"), Mod("a")).DeterministicOrder));

suite.Add("profile round trip", () => Equal("test", ProfileStore.Deserialize(ProfileStore.Serialize(Profile())).ProfileId));
suite.Add("profile rejects schema", () => Throws<FormatException>(() => ProfileStore.Serialize(Profile() with { SchemaVersion = 2 })));
suite.Add("profile rejects duplicate install", () => Throws<FormatException>(() => ProfileStore.Serialize(Profile() with { Mods = [PMod("x", 0), PMod("x", 1)] })));
suite.Add("profile rejects duplicate priority", () => Throws<FormatException>(() => ProfileStore.Serialize(Profile() with { Mods = [PMod("x", 0), PMod("y", 0)] })));
suite.Add("profile contains no root path", () => False(ProfileStore.Serialize(Profile()).Contains("RootPath", StringComparison.OrdinalIgnoreCase)));
suite.Add("desktop preferences default to apply profile launch", () => Equal(LauncherLaunchAction.ApplyProfileAndLaunch, new DesktopPreferences().DefaultLaunchAction));
suite.Add("desktop preferences persist launch action and developer mode", () => WithDirectory(root => { var store=new DesktopPreferencesStore(ManagerStoragePaths.Resolve(root));store.Save(new(DefaultLaunchAction:LauncherLaunchAction.OpenOfficialLauncher,LauncherDeveloperMode:true));var loaded=store.Load();Equal(LauncherLaunchAction.OpenOfficialLauncher,loaded.DefaultLaunchAction);True(loaded.LauncherDeveloperMode); }));

suite.Add("archive inventories class", () => WithJar(new Dictionary<string, byte[]> { ["a/B.class"] = [1, 2, 3] }, path => Equal("a.B", ModScanner.InspectJar(path).Classes.Single().ClassName)));
suite.Add("archive inventories descriptor", () => WithJar(new Dictionary<string, byte[]> { ["META-INF/choir/mod.json"] = Encoding.UTF8.GetBytes("{}") }, path => Equal("META-INF/choir/mod.json", ModScanner.InspectJar(path).DescriptorEntries.Single())));
suite.Add("archive rejects zip slip", () => WithJar(new Dictionary<string, byte[]> { ["../bad.class"] = [1] }, path => False(ModScanner.InspectJar(path).IsValid)));
suite.Add("archive malformed isolated", () => WithTempFile([1, 2, 3], path => False(ModScanner.InspectJar(path).IsValid), ".jar"));
suite.Add("archive never loads class", () => WithJar(new Dictionary<string, byte[]> { ["evil/StaticInitializer.class"] = [0, 1] }, path => Equal(1, ModScanner.InspectJar(path).Classes.Count)));
suite.Add("archive oversized descriptor rejected", () => WithJar(new Dictionary<string, byte[]> { ["META-INF/choir/mod.json"] = new byte[1024 * 1024 + 1] }, path => False(ModScanner.InspectJar(path).IsValid)));
suite.Add("archive entry limit enforced", () => WithManyEntryJar(ModScanner.MaxArchiveEntries + 1, path => False(ModScanner.InspectJar(path).IsValid)));

suite.Add("conflict duplicate launcher entry", () => HasConflict(ConflictAnalyzer.Analyze([Mod("a")], ["a", "a"], 71), "duplicate-launcher-entry"));
suite.Add("conflict duplicate logical ID", () => HasConflict(ConflictAnalyzer.Analyze([Mod("a"), Mod("a", source: ModSourceType.Workshop)], ["a"], 71), "duplicate-logical-id"));
suite.Add("conflict exact content duplicate", () => HasConflict(ConflictAnalyzer.Analyze([Mod("a"), Mod("b", source: ModSourceType.Workshop)], ["a", "b"], 71), "exact-content-duplicate"));
suite.Add("conflict missing dependency", () => HasConflict(ConflictAnalyzer.Analyze([Mod("a", requires: [new("missing", "*", false)])], ["a"], 71), "missing-dependency"));
suite.Add("conflict declared incompatibility", () => HasConflict(ConflictAnalyzer.Analyze([Mod("a", incompatible: ["b"]), Mod("b")], ["a", "b"], 71), "declared-incompatibility"));
suite.Add("conflict provider collision", () => HasConflict(ConflictAnalyzer.Analyze([Mod("a", provider: "same"), Mod("b", provider: "same")], ["a", "b"], 71), "options-provider-collision"));
suite.Add("conflict game version mismatch", () => HasConflict(ConflictAnalyzer.Analyze([Mod("a", metadataMajor: 70)], ["a"], 71), "game-version-mismatch"));
suite.Add("conflict no false provider collision", () => False(ConflictAnalyzer.Analyze([Mod("a", provider: "one"), Mod("b", provider: "two")], ["a", "b"], 71).Any(x => x.Category == "options-provider-collision")));
suite.Add("conflict missing configured mod", () => HasConflict(ConflictAnalyzer.Analyze([Mod("a")], ["a", "missing"], 71), "missing-installation"));
suite.Add("conflict identical class bytes", () => HasConflict(ConflictAnalyzer.Analyze([WithClasses(Mod("a"), ("same.Type", "aa")), WithClasses(Mod("b"), ("same.Type", "aa"))], ["a", "b"], 71), "identical-class-duplicate"));
    suite.Add("conflict different class bytes", () => HasConflict(ConflictAnalyzer.Analyze([WithClasses(Mod("a"), ("same.Type", "aa")), WithClasses(Mod("b"), ("same.Type", "bb"))], ["a", "b"], 71), "class-collision"));
    suite.Add("conflict winner is larger profile priority", () =>
    {
        var low = WithClasses(Mod("low", priority: 0), ("same.Type", "aa"));
        var high = WithClasses(Mod("high", priority: 1), ("same.Type", "bb"));
        var conflict = ConflictAnalyzer.Analyze([low, high], ["high", "low"], 71).Single(x => x.Category == "class-collision");
        Equal(high.InstallationId, conflict.CurrentWinner);
    });
suite.Add("conflict vanilla shadow blocking", () => HasConflict(ConflictAnalyzer.Analyze([WithClasses(Mod("a"), ("settlement.Test", "aa")), WithClasses(Mod("b"), ("settlement.Test", "bb"))], ["a", "b"], 71), "vanilla-shadow-collision"));
suite.Add("conflict embedded Choir framework", () => HasConflict(ConflictAnalyzer.Analyze([WithClasses(Mod("consumer"), ("choir.api.Api", "aa"))], ["consumer"], 71), "embedded-choir-framework"));
suite.Add("conflict legacy modoptions package", () => HasConflict(ConflictAnalyzer.Analyze([WithClasses(Mod("consumer"), ("modoptions.Api", "aa"))], ["consumer"], 71), "legacy-modoptions-package"));
suite.Add("conflict duplicate JAR artifact", () => HasConflict(ConflictAnalyzer.Analyze([WithJarHash(Mod("a"), "aa"), WithJarHash(Mod("b"), "aa")], ["a", "b"], 71), "duplicate-jar-artifact"));
suite.Add("conflict unsupported Choir API", () => HasConflict(ConflictAnalyzer.Analyze([WithChoirApi(Mod("consumer"), ">=1.0.0"), Mod("choir.framework", version: "0.3.0")], ["consumer", "choir.framework"], 71), "unsupported-choir-api"));
suite.Add("conflict duplicate stable ID", () => HasConflict(ConflictAnalyzer.Analyze([WithStableId(Mod("a"), "resource", "DUP"), WithStableId(Mod("b"), "resource", "DUP")], ["a", "b"], 71), "stable-id-collision"));
suite.Add("conflict duplicate race ID", () => HasConflict(ConflictAnalyzer.Analyze([WithStableId(Mod("a"), "race", "DUP_RACE"), WithStableId(Mod("b"), "race", "DUP_RACE")], ["a", "b"], 71), "stable-id-collision"));
suite.Add("data disjoint keys classified", () => WithDataPair("A: 1", "B: 2", conflicts => HasConflict(conflicts, "data-disjoint-keys")));
suite.Add("data conflicting keys classified", () => WithDataPair("A: 1", "A: 2", conflicts => HasConflict(conflicts, "data-conflicting-keys")));
suite.Add("data identical keys classified", () => WithDataPair("A: 1", "A: 1\nB: 2", conflicts => HasConflict(conflicts, "data-identical-keys")));
suite.Add("order suggestion puts dependency first", () => EqualSeq(new[] { "choir.framework", "fixture" }, OrderSuggester.Suggest([Mod("fixture", folder: "fixture", requires: [new("choir.framework", "*", false)]), Mod("choir.framework", folder: "choir.framework")], ["fixture", "choir.framework"], Graph(Mod("fixture", requires: [new("choir.framework", "*", false)]), Mod("choir.framework")))));

suite.Add("sandbox writes inside root", () => WithDirectory(root => { var target = Path.Combine(root, "copy.txt"); File.WriteAllText(target, RawSettings("A")); new SandboxSettingsWriter(root).SimulateAtomicWrite(target, File.ReadAllText(target), ["B"]); EqualSeq(new[] { "B" }, LauncherSettingsDocument.Parse(File.ReadAllText(target)).EnabledMods); }));
suite.Add("sandbox rejects outside root", () => WithDirectory(root => WithDirectory(outside => Throws<UnauthorizedAccessException>(() => new SandboxSettingsWriter(root).SimulateAtomicWrite(Path.Combine(outside, "copy.txt"), RawSettings("A"), ["B"])))));
suite.Add("sandbox preserves unknown fields", () => WithDirectory(root => { var target = Path.Combine(root, "copy.txt"); new SandboxSettingsWriter(root).SimulateAtomicWrite(target, RawSettings("A"), ["B"]); True(File.ReadAllText(target).Contains("UNKNOWN: 9", StringComparison.Ordinal)); }));
suite.Add("sandbox creates backup", () => WithDirectory(root => { var target = Path.Combine(root, "copy.txt"); File.WriteAllText(target, RawSettings("A")); new SandboxSettingsWriter(root).SimulateAtomicWrite(target, File.ReadAllText(target), ["B"]); Equal(1, Directory.GetFiles(root, "copy.txt.backup-*").Length); }));
suite.Add("sandbox rollback restores original", () => WithDirectory(root => { var target = Path.Combine(root, "copy.txt"); File.WriteAllText(target, RawSettings("A")); var before = Hashing.Sha256File(target); Throws<InvalidDataException>(() => new SandboxSettingsWriter(root).SimulateAtomicWrite(target, File.ReadAllText(target), ["B"], _ => false)); Equal(before, Hashing.Sha256File(target)); }));
suite.Add("redacted scan excludes physical paths", () => False(ProfileStore.ExportRedactedScan(Report([Mod("a")])).Contains("C:\\private", StringComparison.Ordinal)));

suite.Add("scanner discovers local mod", () => WithScan((report, _) => Equal(1, report.Mods.Count)));
suite.Add("scanner reads enabled order", () => WithScan((report, _) => EqualSeq(new[] { "Example" }, report.EnabledOrder)));
suite.Add("scanner gives first priority zero", () => WithScan((report, _) => Equal(0, report.Mods.Single().Priority)));
suite.Add("scanner chooses V71", () => WithScan((report, _) => Equal(71, report.Mods.Single().SelectedMajorVersion)));
suite.Add("scanner hashes content", () => WithScan((report, _) => Equal(64, report.Mods.Single().ContentFingerprint.Length)));
    suite.Add("scanner priority rule explicit", () => WithScan((report, _) => { True(report.PriorityRule.Contains("Priority 1 is lowest", StringComparison.Ordinal)); True(report.PriorityRule.Contains("Larger priority numbers are higher", StringComparison.Ordinal)); }));
suite.Add("scanner does not alter settings", () => WithScan((_, settings) => Equal(settings.Before, Hashing.Sha256File(settings.Path))));
suite.Add("scanner discovers Workshop mod", ScannerDiscoversWorkshop);
suite.Add("scanner installation identity stable", ScannerIdentityStable);
suite.Add("reversed launcher order preserved", () => EqualSeq(new[] { "B", "A" }, Settings("B", "A").EnabledMods));

suite.Add("live LauncherSettings remains unchanged", LiveSettingsUnchanged);
suite.Add("frozen Choir 0.2 baseline remains unchanged", () => FrozenTreeUnchanged("CorePlatform-0.2.0"));
suite.Add("frozen Choir 0.3 baseline remains unchanged", () => FrozenTreeUnchanged("CorePlatform-0.3.0"));
suite.Add("test process does not launch Songs of Syx", () => False(System.Diagnostics.Process.GetProcesses().Any(p => p.ProcessName.Contains("SongsofSyx", StringComparison.OrdinalIgnoreCase) && p.StartTime > startedAt)));

suite.Add("manager profile retains complete order", () => EqualSeq(new[] { "A", "B", "C" }, MProfile(Entry("A", true), Entry("B", false), Entry("C", true)).Mods.Select(x => x.SourceId)));
    suite.Add("manager official order reverses enabled profile order", () => EqualSeq(new[] { "C", "A" }, MProfile(Entry("A", true), Entry("B", false), Entry("C", true)).EffectiveOfficialOrder));
suite.Add("disable retains profile position", () => { var s = new ProfileEditorSession(MProfile(Entry("A", true), Entry("B", true))); s.SetEnabled(["A"], false); EqualSeq(new[] { "A", "B" }, s.Current.Mods.Select(x => x.EntryId)); });
suite.Add("reenable restores profile position", () => { var s = new ProfileEditorSession(MProfile(Entry("A", false), Entry("B", true))); s.SetEnabled(["A"], true); EqualSeq(new[] { "A", "B" }, s.Current.Mods.Select(x => x.EntryId)); });
suite.Add("missing profile entry retained", () => Equal(ProfileResolutionStatus.Missing, ProfileResolver.Resolve(MProfile(Entry("A", true)), []).Entries.Single().Status));
suite.Add("ambiguous profile entry retained", () => Equal(ProfileResolutionStatus.Ambiguous, ProfileResolver.Resolve(MProfile(Entry("A", true) with { SourceId = "missing", LogicalModId = "shared" }), [Mod("shared"), Mod("shared", source: ModSourceType.Workshop)]).Entries.Single().Status));
suite.Add("relink resolves ambiguity", () => { var a = Mod("shared"); var profile = MProfile(Entry("A", true) with { SourceId = "missing", LogicalModId = "shared" }); var s = new ProfileEditorSession(profile); s.Relink("A", a); Equal(ProfileResolutionStatus.Resolved, ProfileResolver.Resolve(s.Current, [a]).Entries.Single().Status); });
suite.Add("manager profile serialization deterministic", () => { var p = MProfile(Entry("A", true)); Equal(ManagerProfileRepository.Serialize(p), ManagerProfileRepository.Serialize(p)); });
suite.Add("manager profile unknown field preserved", () => { var json = ManagerProfileRepository.Serialize(MProfile(Entry("A", true))).TrimEnd().TrimEnd('}') + ",\n\"futureField\":{\"x\":1}\n}"; var round = ManagerProfileRepository.Serialize(ManagerProfileRepository.Deserialize(json)); True(round.Contains("futureField", StringComparison.Ordinal)); });
suite.Add("manager profile export round trip", () => WithDirectory(root => { var repo = new ManagerProfileRepository(ManagerStoragePaths.Resolve(Path.Combine(root, "store"))); var path = Path.Combine(root, "export.json"); repo.Export(MProfile(Entry("A", true)), path); Equal("manager-test", repo.Import(path).ProfileId); }));
suite.Add("manager profile export path redaction", () => WithDirectory(root => { var repo = new ManagerProfileRepository(ManagerStoragePaths.Resolve(Path.Combine(root, "store"))); var path = Path.Combine(root, "export.json"); repo.Export(MProfile(Entry("A", true)), path); False(File.ReadAllText(path).Contains(root, StringComparison.OrdinalIgnoreCase)); }));
    suite.Add("manager profile unsafe import rejected", () => WithDirectory(root => { var repo = new ManagerProfileRepository(ManagerStoragePaths.Resolve(Path.Combine(root, "store"))); var path = Path.Combine(root, "bad.json"); File.WriteAllText(path, ManagerProfileRepository.Serialize(MProfile(Entry("A", true) with { Notes = "C:\\Users\\Private" }))); Throws<InvalidDataException>(() => repo.Import(path)); }));
    suite.Add("manager profile duplicate resets application", () => Null(ProfileFactory.Duplicate(MProfile(Entry("A", true)) with { LastSuccessfulApplication = new(DateTimeOffset.UnixEpoch, new string('a',64), "b", "c") }, "copy", "Copy").LastSuccessfulApplication));
    suite.Add("priority one is lowest", () => { Equal(1, ModPriorityOrder.ToDisplayPriority(0)); False(ModPriorityOrder.IsHigherPriority(0, 1)); });
    suite.Add("larger priority number is higher", () => { Equal(7, ModPriorityOrder.ToDisplayPriority(6)); True(ModPriorityOrder.IsHigherPriority(6, 1)); });
    suite.Add("profile order reverses at official boundary", () => EqualSeq(new[] { "overhaul", "content", "framework" }, ModPriorityOrder.ToOfficialOrder(new[] { "framework", "content", "overhaul" })));
    suite.Add("official index maps to profile priority", () => { Equal(2, ModPriorityOrder.FromOfficialIndex(0, 3)); Equal(0, ModPriorityOrder.FromOfficialIndex(2, 3)); });
    suite.Add("manager schema two migration preserves visible row order", () =>
    {
        var legacy = MProfile(Entry("framework", true), Entry("overhaul", true)) with { SchemaVersion = 2 };
        var migrated = ManagerProfileRepository.Deserialize(JsonSerializer.Serialize(legacy, ManagerJson.Options));
        Equal(ManagerProfileValidator.CurrentSchemaVersion, migrated.SchemaVersion);
        EqualSeq(new[] { "framework", "overhaul" }, migrated.Mods.Select(x => x.SourceId));
        EqualSeq(new[] { "overhaul", "framework" }, migrated.EffectiveOfficialOrder);
    });
    suite.Add("profile factory imports official state as low-to-high", () =>
    {
        var high = Mod("high", folder: "High");
        var low = Mod("low", folder: "Low");
        var profile = ProfileFactory.FromOfficialState("imported", "Imported", Report([high, low], ["High", "Low"]));
        EqualSeq(new[] { "Low", "High" }, profile.Mods.Where(x => x.Enabled).Select(x => x.SourceId));
    });
    suite.Add("default profile uses reserved identity", () => { var p = ProfileFactory.DefaultFromOfficialState(Report([])); Equal("default", p.ProfileId); Equal("Default", p.DisplayName); });
    suite.Add("only reserved default ID is permanent", () => { False(DefaultProfilePolicy.IsDefault(ProfileFactory.Empty("new-profile-20260715231248", "New Profile", "0.71.44"))); True(DefaultProfilePolicy.IsDefault(ProfileFactory.Empty("default", "Renamed Default", "0.71.44"))); });
suite.Add("default profile is created beside existing profiles", () => WithDirectory(root => { var repo = new ManagerProfileRepository(ManagerStoragePaths.Resolve(root)); var existing = MProfile(Entry("A", true)); repo.Save(existing); var created = DefaultProfilePolicy.Ensure(repo, repo.LoadAll(), Report([Mod("A")])); Equal("default", created.ProfileId); Equal(2, repo.LoadAll().Count); }));
suite.Add("default profile creation is idempotent", () => WithDirectory(root => { var repo = new ManagerProfileRepository(ManagerStoragePaths.Resolve(root)); var first = DefaultProfilePolicy.Ensure(repo, repo.LoadAll(), Report([])); var second = DefaultProfilePolicy.Ensure(repo, repo.LoadAll(), Report([])); Equal(first.ProfileId, second.ProfileId); Equal(1, repo.LoadAll().Count); }));
suite.Add("remove records restorable profile entry", () => { var s = Session("A", "B"); True(s.Remove(["A"])); EqualSeq(new[] { "B" }, Ids(s)); Equal("A", s.Current.RemovedMods.Single().EntryId); True(s.Undo()); EqualSeq(new[] { "A", "B" }, Ids(s)); Equal(0, s.Current.RemovedMods.Count); });
suite.Add("removed profile entry restores previous state", () => { var s = new ProfileEditorSession(MProfile(Entry("A", true), Entry("B", false))); s.Remove(["B"]); True(s.RestoreRemoved(["B"])); EqualSeq(new[] { "A", "B" }, Ids(s)); False(s.Current.Mods.Single(x => x.EntryId == "B").Enabled); Equal(0, s.Current.RemovedMods.Count); });
suite.Add("removed profile entries survive serialization", () => { var s = Session("A"); s.Remove(["A"]); var round = ManagerProfileRepository.Deserialize(ManagerProfileRepository.Serialize(s.Current)); Equal("A", round.RemovedMods.Single().EntryId); });
suite.Add("new installation is automatically appended disabled", () => { var a = Mod("A"); var b = Mod("B", source: ModSourceType.Workshop); var result = ProfileInventoryReconciler.Reconcile(MProfile(EntryFrom(a, true)), [a, b]); Equal(1, result.AddedEntries.Count); Equal("B", result.Profile.Mods[^1].LogicalModId); False(result.Profile.Mods[^1].Enabled); });
suite.Add("new installation is appended to every profile", () => { var installed = Mod("New", source: ModSourceType.Workshop); var profiles = new[] { MProfile(Entry("A", true)), MProfile(Entry("B", false)) with { ProfileId = "manager-test-two" } }; var reconciled = profiles.Select(profile => ProfileInventoryReconciler.Reconcile(profile, [installed]).Profile).ToArray(); True(reconciled.All(profile => profile.Mods.Any(entry => entry.Source == installed.Source && entry.SourceId == installed.SourceId))); });
suite.Add("automatic inventory respects intentional removal", () => { var installed = Mod("A", source: ModSourceType.Workshop); var profile = MProfile(EntryFrom(installed, true)); var session = new ProfileEditorSession(profile); session.Remove([profile.Mods.Single().EntryId]); var result = ProfileInventoryReconciler.Reconcile(session.Current, [installed]); Equal(0, result.AddedEntries.Count); Equal(0, result.Profile.Mods.Count); });
suite.Add("automatic inventory recognizes updated source identity", () => { var before = Mod("A", source: ModSourceType.Workshop); var updated = Mod("A", source: ModSourceType.Workshop); var result = ProfileInventoryReconciler.Reconcile(MProfile(EntryFrom(before, true)), [updated]); Equal(0, result.AddedEntries.Count); });

suite.Add("single row move before", () => { var s = Session("A", "B", "C"); s.MoveBefore(["C"], "A"); EqualSeq(new[] { "C", "A", "B" }, Ids(s)); });
suite.Add("single row move after", () => { var s = Session("A", "B", "C"); s.MoveAfter(["A"], "C"); EqualSeq(new[] { "B", "C", "A" }, Ids(s)); });
suite.Add("noncontiguous block move preserves relative order", () => { var s = Session("A", "B", "C", "D", "E", "F"); s.MoveBefore(["B", "D", "F"], "A"); EqualSeq(new[] { "B", "D", "F", "A", "C", "E" }, Ids(s)); });
suite.Add("block move retains selected entry IDs", () => { var s=Session("A","B","C","D");s.MoveBefore(["B","D"],"A");EqualSeq(new[]{"B","D"},s.CurrentSelection); });
suite.Add("block move preserves enabled state", () => { var s = new ProfileEditorSession(MProfile(Entry("A", true), Entry("B", false), Entry("C", true))); s.MoveAfter(["A", "B"], "C"); EqualSeq(new[] { true, false }, s.Current.Mods.Skip(1).Select(x => x.Enabled)); });
suite.Add("move selected target is no op", () => { var s = Session("A", "B"); False(s.MoveBefore(["A"], "A")); Equal(0, s.UndoCount); });
suite.Add("unchanged top move is no op", () => { var s = Session("A", "B"); False(s.MoveToTop(["A"])); Equal(0, s.UndoCount); });
suite.Add("one drag one undo", () => { var s = Session("A", "B", "C"); s.MoveBefore(["C"], "A"); Equal(1, s.UndoCount); s.Undo(); EqualSeq(new[] { "A", "B", "C" }, Ids(s)); });
suite.Add("redo restores drag", () => { var s = Session("A", "B", "C"); s.MoveBefore(["C"], "A"); s.Undo(); s.Redo(); EqualSeq(new[] { "C", "A", "B" }, Ids(s)); });
suite.Add("undo exposes operation selection", () => { var s=Session("A","B","C");s.MoveBefore(["C"],"A");s.Undo();EqualSeq(new[]{"C"},s.CurrentSelection); });
suite.Add("divergent edit clears redo", () => { var s = Session("A", "B", "C"); s.MoveBefore(["C"], "A"); s.Undo(); s.SetEnabled(["A"], false); False(s.CanRedo); });
suite.Add("move top preserves selected order", () => { var s = Session("A", "B", "C", "D"); s.MoveToTop(["B", "D"]); EqualSeq(new[] { "B", "D", "A", "C" }, Ids(s)); });
suite.Add("move bottom preserves selected order", () => { var s = Session("A", "B", "C", "D"); s.MoveToBottom(["A", "C"]); EqualSeq(new[] { "B", "D", "A", "C" }, Ids(s)); });
suite.Add("accepted suggestion is one undo", () => { var s = Session("dependent", "dependency"); s.ApplySuggestedOrder(["dependency", "dependent"]); Equal(1, s.UndoCount); EqualSeq(new[] { "dependency", "dependent" }, Ids(s)); });

suite.Add("dependency enable plan finds disabled requirement", () => { var dependency = Mod("dependency"); var dependent = Mod("dependent", requires: [new("dependency", "*", false)]); var profile = MProfile(EntryFrom(dependent, true), EntryFrom(dependency, false)); var resolved = ProfileResolver.Resolve(profile, [dependent, dependency]); Equal("dependency", ProfileDependencyPlanner.Plan(resolved, [profile.Mods[0].EntryId], true).RequiredEntryIds.Single()); });
suite.Add("dependency disable plan finds dependents", () => { var dependency = Mod("dependency"); var dependent = Mod("dependent", requires: [new("dependency", "*", false)]); var profile = MProfile(EntryFrom(dependent, true), EntryFrom(dependency, true)); var resolved = ProfileResolver.Resolve(profile, [dependent, dependency]); Equal("dependent", ProfileDependencyPlanner.Plan(resolved, [profile.Mods[1].EntryId], false).DependentEntryIds.Single()); });
suite.Add("suggested order leaves disabled positions", () => { var p = MProfile(Entry("dependent", true), Entry("disabled", false), Entry("dependency", true)); var s = new ProfileEditorSession(p); s.ApplySuggestedOrder(["dependency", "dependent"]); Equal("disabled", s.Current.Mods[1].EntryId); });

    suite.Add("official comparison detects priority", () => { var a = Mod("A"); var b = Mod("B"); var p = MProfile(EntryFrom(b,true),EntryFrom(a,true)); var c = OfficialStateComparer.Compare(ProfileResolver.Resolve(p,[a,b]), Settings("B","A"), [a,b]); True(c.Differences.Any(x=>x.Kind==OfficialDifferenceKind.DifferentPriority)); });
suite.Add("official comparison identical", () => { var a=Mod("A"); var p=MProfile(EntryFrom(a,true)); True(OfficialStateComparer.Compare(ProfileResolver.Resolve(p,[a]),Settings("A"),[a]).EffectiveStatesIdentical); });
    suite.Add("apply preview exact MODS replacement", () => WithApplyFixture(f => EqualSeq(new[] { "B" }, f.Preview.ProposedOrder)));
    suite.Add("apply preview writes highest profile priority first", () => WithDirectory(root =>
    {
        var path = Path.Combine(root, "settings.txt");
        File.WriteAllText(path, RawSettings());
        var low = Mod("framework", folder: "Framework");
        var high = Mod("overhaul", folder: "Overhaul");
        var resolved = ProfileResolver.Resolve(MProfile(EntryFrom(low, true), EntryFrom(high, true)), [low, high]);
        var preview = ApplyPreviewService.Create(path, resolved, [], Path.Combine(root, "backups"));
        EqualSeq(new[] { "Overhaul", "Framework" }, preview.ProposedOrder);
    }));
suite.Add("apply preview preserves unknown bytes", () => WithApplyFixture(f => Equal(f.Preview.CurrentUnrelatedSha256, f.Preview.ProposedUnrelatedSha256)));
suite.Add("apply preview identifies added removed", () => WithApplyFixture(f => { Equal("B", f.Preview.Added.Single()); Equal("A", f.Preview.Removed.Single()); }));
suite.Add("apply preview blocks enabled missing", () => WithDirectory(root => { var path=Path.Combine(root,"settings.txt");File.WriteAllText(path,RawSettings("A"));var rp=ProfileResolver.Resolve(MProfile(Entry("missing",true)),[]);var preview=ApplyPreviewService.Create(path,rp,[],Path.Combine(root,"backups"));False(preview.CanApplyTechnically); }));
suite.Add("apply preview blocks duplicate official IDs", () => WithDirectory(root => { var path=Path.Combine(root,"settings.txt");File.WriteAllText(path,RawSettings("A"));var a=Mod("A");var rp=ProfileResolver.Resolve(MProfile(EntryFrom(a,true),EntryFrom(a,true) with {EntryId="A2"}),[a]);var preview=ApplyPreviewService.Create(path,rp,[],Path.Combine(root,"backups"));False(preview.CanApplyTechnically); }));
suite.Add("compatibility signature deterministic", () => WithApplyFixture(f => { var conflict=TestConflict();var p1=ApplyPreviewService.Create(f.Path,f.Resolved,[conflict],f.Backups);var p2=ApplyPreviewService.Create(f.Path,f.Resolved,[conflict],f.Backups);Equal(p1.ConflictSignature,p2.ConflictSignature); }));
suite.Add("new compatibility warning requires one confirmation", () => WithApplyFixture(f => { var preview=ApplyPreviewService.Create(f.Path,f.Resolved,[TestConflict()],f.Backups);True(preview.RequiresCompatibilityAcknowledgement);False(preview.CompatibilityWarningAcknowledged); }));
suite.Add("acknowledged compatibility warning does not repeat", () => WithApplyFixture(f => { var conflict=TestConflict();var first=ApplyPreviewService.Create(f.Path,f.Resolved,[conflict],f.Backups);var profile=f.Resolved.Profile with {AcknowledgedWarnings=[new(first.ConflictSignature,DateTimeOffset.UnixEpoch,"accepted")]};var repeated=ApplyPreviewService.Create(f.Path,f.Resolved with {Profile=profile},[conflict],f.Backups);True(repeated.CompatibilityWarningAcknowledged);False(repeated.RequiresCompatibilityAcknowledgement); }));
suite.Add("compatibility warning identifies responsible mod", () => { var installation=Mod("fixing.syx",folder:"3717990329",source:ModSourceType.Workshop) with {Metadata=Info() with {Name="Fixing the Syx"}};var finding=TestConflict() with {Category="malformed-metadata",Severity=Severity.Medium,InvolvedMods=[installation.InstallationId],Target="_Info.txt",Explanation="Metadata is missing or malformed.",RecommendedAction="Repair _Info.txt."};var warning=CompatibilityWarningFormatter.Format([finding],[installation]);True(warning.Contains("Fixing the Syx (Workshop 3717990329)",StringComparison.Ordinal));True(warning.Contains("Metadata is missing or malformed.",StringComparison.Ordinal));True(warning.Contains("Malformed metadata",StringComparison.Ordinal)); });

suite.Add("writer applies sandbox configuration", () => WithApplyFixture(f => { var result=f.Writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult();True(result.Success);EqualSeq(new[]{"B"},LauncherSettingsDocument.Parse(File.ReadAllText(f.Path)).EnabledMods); }));
suite.Add("writer creates exact backup", () => WithApplyFixture(f => { var before=Hashing.Sha256File(f.Path);var result=f.Writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult();var backup=f.Store.List().Single();Equal(before,Hashing.Sha256File(backup.DataPath));Equal(result.BackupId!,backup.Metadata.BackupId); }));
suite.Add("writer rejects stale preview", () => WithApplyFixture(f => { File.AppendAllText(f.Path,"\nCHANGED: true,");var result=f.Writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult();False(result.Success);True(result.Diagnostics.Any(x=>x.Contains("changed after preview",StringComparison.OrdinalIgnoreCase))); }));
suite.Add("writer blocks running game", () => WithApplyFixture(f => { var writer=new OfficialConfigurationWriter(new SandboxConfigurationWriteGuard(f.Root),new FakeProcesses(true),new NamedMutexApplyLockFactory(),f.Store);var result=writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult();False(result.Success); }));
suite.Add("writer blocks held lock", () => WithApplyFixture(f => { var writer=new OfficialConfigurationWriter(new SandboxConfigurationWriteGuard(f.Root),new FakeProcesses(false),new FakeLockFactory(false),f.Store);var result=writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult();False(result.Success); }));
suite.Add("writer requires new compatibility warning confirmation", () => WithApplyFixture(f => { var p=ApplyPreviewService.Create(f.Path,f.Resolved,[TestConflict()],f.Backups);var result=f.Writer.ApplyAsync(p,null).GetAwaiter().GetResult();False(result.Success); }));
suite.Add("writer accepts confirmed compatibility warning signature", () => WithApplyFixture(f => { var p=ApplyPreviewService.Create(f.Path,f.Resolved,[TestConflict()],f.Backups);var result=f.Writer.ApplyAsync(p,p.ConflictSignature).GetAwaiter().GetResult();True(result.Success); }));
suite.Add("writer does not repeat acknowledged compatibility warning", () => WithApplyFixture(f => { var conflict=TestConflict();var first=ApplyPreviewService.Create(f.Path,f.Resolved,[conflict],f.Backups);var profile=f.Resolved.Profile with {AcknowledgedWarnings=[new(first.ConflictSignature,DateTimeOffset.UnixEpoch,"accepted")]};var repeated=ApplyPreviewService.Create(f.Path,f.Resolved with {Profile=profile},[conflict],f.Backups);True(f.Writer.ApplyAsync(repeated,null).GetAwaiter().GetResult().Success); }));
suite.Add("writer rolls back post replace failure", () => WithApplyFixture(f => { var before=Hashing.Sha256File(f.Path);var writer=new OfficialConfigurationWriter(new SandboxConfigurationWriteGuard(f.Root),new FakeProcesses(false),new NamedMutexApplyLockFactory(),f.Store,new ThrowAt("after-replace"));var result=writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult();False(result.Success);True(result.RollbackSucceeded);Equal(before,Hashing.Sha256File(f.Path)); }));
suite.Add("writer restore reproduces original hash", () => WithApplyFixture(f => { var original=Hashing.Sha256File(f.Path);True(f.Writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult().Success);var backup=f.Store.List().Single();True(f.Writer.RestoreAsync(f.Path,backup).GetAwaiter().GetResult().Success);Equal(original,Hashing.Sha256File(f.Path)); }));
suite.Add("writer audit metadata recorded", () => WithApplyFixture(f => { f.Writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult();Equal("APPLIED",f.Store.List().Single().Metadata.Result); }));
suite.Add("sandbox guard rejects live settings", () => WithDirectory(root => Throws<UnauthorizedAccessException>(() => new SandboxConfigurationWriteGuard(root).Authorize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"songsofsyx","settings","LauncherSettings.txt")))));
suite.Add("final backup cannot be deleted", () => WithApplyFixture(f => { f.Writer.ApplyAsync(f.Preview,null).GetAwaiter().GetResult();Throws<InvalidOperationException>(()=>f.Store.Delete(f.Store.List().Single())); }));
suite.Add("profile repository writes atomic backup", () => WithDirectory(root => { var repo=new ManagerProfileRepository(ManagerStoragePaths.Resolve(root));var p=MProfile(Entry("A",true));repo.Save(p);repo.Save(p with {DisplayName="Changed"});True(Directory.EnumerateFiles(Path.Combine(root,"profiles",".backups"),"*.bak").Any()); }));

suite.Add("desktop view model exposes explicit launch choices", () => WithDirectory(root => { var vm=new MainWindowViewModel(root);True(vm.LaunchEnabled);True(vm.LaunchExplanation.Contains("official launcher",StringComparison.OrdinalIgnoreCase));True(vm.LaunchExplanation.Contains("never changes",StringComparison.OrdinalIgnoreCase)); }));
suite.Add("game version is read from JAR metadata without loading game code", () => WithLaunchFiles((environment,_,_,_) => { var probe=SongsOfSyxGameArtifactInspector.Inspect(environment.GameJarPath);True(probe.StructurallyValid);Equal("0.70.12",probe.Version!.Display);False(probe.KnownBuild); }));
suite.Add("known build catalog recognizes recorded v71.44 hash", () => True(KnownGameBuildCatalog.IdentifyJar(KnownGameBuildCatalog.V7144JarSha256) is not null));
suite.Add("launch resolver accepts unrecognized patched direct game", () => WithLaunchFiles((environment,directHash,_,jarHash) => { var target=new SongsOfSyxGameLaunchTargetResolver().Resolve(environment,GameLaunchRoute.DirectGame);Equal("SyxWithout.exe",Path.GetFileName(target.ExecutablePath));Equal(directHash,target.ExecutableSha256);Equal(jarHash,target.GameJarSha256);Equal("0.70.12",target.GameVersion!);False(target.KnownGameBuild);False(target.KnownExecutable); }));
suite.Add("launch resolver accepts unrecognized patched official launcher", () => WithLaunchFiles((environment,_,officialHash,_) => { var target=new SongsOfSyxGameLaunchTargetResolver().Resolve(environment,GameLaunchRoute.OfficialLauncher);Equal("SongsofSyx.exe",Path.GetFileName(target.ExecutablePath));Equal(officialHash,target.ExecutableSha256); }));
suite.Add("launch resolver rejects non-PE executable", () => WithLaunchFiles((environment,_,_,_) => { File.WriteAllBytes(Path.Combine(environment.GameRoot!,"SyxWithout.exe"),[1,2,3,4]);Throws<InvalidDataException>(() => new SongsOfSyxGameLaunchTargetResolver().Resolve(environment,GameLaunchRoute.DirectGame)); }));
suite.Add("launch resolver rejects structurally invalid game JAR", () => WithLaunchFiles((environment,_,_,_) => { File.WriteAllBytes(environment.GameJarPath!,[1,2,3,4]);Throws<InvalidDataException>(() => new SongsOfSyxGameLaunchTargetResolver().Resolve(environment,GameLaunchRoute.DirectGame)); }));
suite.Add("launch service starts exactly once with current settings hash", () => WithLaunchFiles((environment,_,_,_) => { var starter=new FakeGameStarter();var result=new GameLaunchService(new FakeLaunchTargetResolver(),new FakeProcesses(false),new FakeLockFactory(true),starter).Launch(environment,GameLaunchRoute.DirectGame);True(result.Success);Equal(1,starter.Count);Equal(Hashing.Sha256File(environment.LauncherSettingsPath),result.ConfigurationSha256); }));
suite.Add("launch service blocks running game before process start", () => WithLaunchFiles((environment,_,_,_) => { var starter=new FakeGameStarter();var result=new GameLaunchService(new FakeLaunchTargetResolver(),new FakeProcesses(true),new FakeLockFactory(true),starter).Launch(environment,GameLaunchRoute.DirectGame);False(result.Success);Equal(0,starter.Count); }));
suite.Add("launch service blocks held configuration lock", () => WithLaunchFiles((environment,_,_,_) => { var starter=new FakeGameStarter();var result=new GameLaunchService(new FakeLaunchTargetResolver(),new FakeProcesses(false),new FakeLockFactory(false),starter).Launch(environment,GameLaunchRoute.DirectGame);False(result.Success);Equal(0,starter.Count); }));
suite.Add("launch service does not parse version-specific settings", () => WithLaunchFiles((environment,_,_,_) => { File.WriteAllText(environment.LauncherSettingsPath,"older or newer launcher settings");var starter=new FakeGameStarter();var result=new GameLaunchService(new FakeLaunchTargetResolver(),new FakeProcesses(false),new FakeLockFactory(true),starter).Launch(environment,GameLaunchRoute.DirectGame);True(result.Success);Equal(1,starter.Count);Equal(Hashing.Sha256File(environment.LauncherSettingsPath),result.ConfigurationSha256); }));
suite.Add("production process starter is disabled in test mode", () => { var prior=Environment.GetEnvironmentVariable("CHOIRLAUNCHER_TEST_MODE");try { Environment.SetEnvironmentVariable("CHOIRLAUNCHER_TEST_MODE","1");Throws<UnauthorizedAccessException>(()=>new WindowsGameProcessStarter().Start(new(GameLaunchRoute.DirectGame,"fixture","unused","unused",new string('a',64),new string('b',64),"0.71.44",true,true,[]))); } finally { Environment.SetEnvironmentVariable("CHOIRLAUNCHER_TEST_MODE",prior); } });
suite.Add("desktop filtered drag disabled", () => WithDirectory(root => { var vm=new MainWindowViewModel(root);vm.SearchText="x";False(vm.CanDrag); }));
suite.Add("desktop headless window constructs", HeadlessWindowConstructs);
suite.Add("desktop window opens before profiles load", HeadlessWindowOpensBeforeProfilesLoad);
suite.Add("desktop toolbar removes redundant move and state buttons", DesktopToolbarIsSimplified);
suite.Add("desktop toolbar groups tools in reading order", DesktopToolbarGroupsToolsInReadingOrder);
suite.Add("desktop windows use the Songs of Syx application icon", DesktopUsesSongsOfSyxWindowIcon);
suite.Add("settings display column contains its controls", SettingsDisplayColumnContainsControls);
suite.Add("desktop launch action exposes verified workflow", DesktopLaunchIsActionable);
suite.Add("desktop launch selector defaults to apply profile", DesktopLaunchSelectorDefaultsToApplyProfile);
suite.Add("desktop launch action uses official logo and ornament", DesktopLaunchUsesVanillaLauncherArt);
suite.Add("desktop uses owner city background without intercepting input", DesktopUsesOwnerBackground);
suite.Add("desktop exposes integrated settings info and language", DesktopExposesGameLauncherFeatures);
suite.Add("desktop mod list requests vertical scrolling", DesktopModListRequestsScrolling);
suite.Add("desktop profile management has dedicated entry point", DesktopProfileManagementEntryPoint);
suite.Add("desktop mod columns expose resize grips", DesktopModColumnsAreResizable);
suite.Add("desktop restored columns cannot overflow details pane", DesktopRestoredColumnsStayContained);
suite.Add("desktop profile actions use explicit wording", DesktopProfileActionsAreExplicit);
suite.Add("desktop uncolored mod names inherit visible foreground", DesktopModNamesInheritForeground);
suite.Add("large profile block move performance", LargeProfilePerformance);
suite.Add("profile append is undoable", () => { var s=Session("A");True(s.Append([Entry("B",false)]));EqualSeq(new[]{"A","B"},Ids(s));True(s.Undo());EqualSeq(new[]{"A"},Ids(s)); });
suite.Add("profile replacement is undoable", () => { var s=Session("A","B");True(s.ReplaceMods([Entry("C",true)],"Replace"));EqualSeq(new[]{"C"},Ids(s));True(s.Undo());EqualSeq(new[]{"A","B"},Ids(s)); });
suite.Add("warning acknowledgement binds exact signature", () => { var s=Session("A");True(s.Acknowledge("sig-a","accepted"));False(s.Current.AcknowledgedWarnings.Any(x=>x.Signature=="sig-b"));False(s.Acknowledge("sig-a","again")); });
suite.Add("manager application log is manager owned", () => WithDirectory(root => { var paths=ManagerStoragePaths.Resolve(root);var log=new ApplicationLog(paths);log.Write("INFO","test","message");True(File.Exists(log.Path));True(Path.GetFullPath(log.Path).StartsWith(Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase)); }));
suite.Add("desktop preferences round trip", () => WithDirectory(root => { var paths=ManagerStoragePaths.Resolve(root);var store=new DesktopPreferencesStore(paths);var expected=new DesktopPreferences(1,"p",1200,700,10,20,true);store.Save(expected);Equal(expected,store.Load()); }));
suite.Add("desktop preferences retain mod column widths", () => WithDirectory(root => { var paths=ManagerStoragePaths.Resolve(root);var store=new DesktopPreferencesStore(paths);double[] widths=[38,42,55,210,170,90,70,80,105,80];store.Save(new DesktopPreferences(1,"p",1200,700,10,20,false,widths));EqualSeq(widths,store.Load().ModListColumnWidths!); }));
suite.Add("named mutex excludes concurrent writer", () => WithDirectory(root => { var factory=new NamedMutexApplyLockFactory();using var first=factory.TryAcquire(Path.Combine(root,"settings"),TimeSpan.FromSeconds(1));True(first.Acquired);using var second=factory.TryAcquire(Path.Combine(root,"settings"),TimeSpan.FromMilliseconds(50));False(second.Acquired); }));
suite.Add("scanner honors cancellation", () => WithDirectory(root => { var local=Path.Combine(root,"local");var workshop=Path.Combine(root,"workshop");var settings=Path.Combine(root,"settings.txt");Directory.CreateDirectory(local);Directory.CreateDirectory(workshop);File.WriteAllText(settings,RawSettings());using var cts=new CancellationTokenSource();cts.Cancel();Throws<OperationCanceledException>(()=>new ModScanner().Scan(new(local,workshop,settings,null,71,"0.71.44"),cts.Token)); }));
suite.Add("cancelled apply writes nothing", () => WithApplyFixture(f => { var before=Hashing.Sha256File(f.Path);using var cts=new CancellationTokenSource();cts.Cancel();Throws<OperationCanceledException>(()=>f.Writer.ApplyAsync(f.Preview,null,cts.Token).GetAwaiter().GetResult());Equal(before,Hashing.Sha256File(f.Path));False(f.Store.List().Any()); }));

return suite.Run();

static LauncherSettingsDocument Settings(params string[] mods) => LauncherSettingsDocument.Parse(RawSettings(mods));
static string RawSettings(params string[] mods) => "VERSION: 71,\r\nUNKNOWN: 9,\r\nMODS: [\r\n" + string.Join("", mods.Select(x => $"\t\"{x}\",\r\n")) + "],\r\n";
static string RawFullSettings(params string[] mods) => "JVM: 0,\r\nDEBUG: 0,\r\nDEVELOPER: 0,\r\nLINEAR: 1,\r\nSHADING: 1,\r\nVSYNC: 1,\r\nVSYNC_ADAPTIVE: 1,\r\nEASY_FONT: 0,\r\nVERSION: 4653100,\r\nMONITOR: 0,\r\nSCREEN_MODE: 2,\r\nFPS_CAP: 0,\r\nFULL_DISPLAY: 0,\r\nWINDOW_WIDTH: 14,\r\nWIDOW_HEIGHT: 14,\r\nWIDOW_SCALE: 0,\r\nWINDOW_DECORATE: 1,\r\nWINDOW_FORCE_HD: 0,\r\nWIN_AUTO_ICONIFY: 1,\r\nWINDOW_FLOAT: 0,\r\nWINDOW_FULL_FULL: 0,\r\nLANGUAGE: \"\",\r\nOPENAL: \"\",\r\nUNKNOWN: 9,\r\nMODS: [\r\n" + string.Join("", mods.Select(x => $"\t\"{x}\",\r\n")) + "],\r\n";
static ModMetadata Info() => MetadataParsers.ParseInfo("VERSION: \"1.2.3\",\nGAME_VERSION_MAJOR: 71,\nGAME_VERSION_MINOR: 44,\nNAME: \"Test\",\nAUTHOR: \"Henrique\",");
static ChoirManifest Manifest() => MetadataParsers.ParseChoirManifest("formatVersion=1\nmodId=fixture.alpha\ndisplayName=Fixture\nversion=0.1.0\nrequires=choir.framework@>=0.2.0\ncapabilities=cap.one");
static DependencyGraphResult Graph(params ModInstallation[] mods) => DependencyGraphResolver.Resolve(mods);
static ModProfile Profile() => new(1, "test", "Test", "0.71.44", [PMod("x", 0)], [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null);
static ProfileMod PMod(string id, int priority) => new(id, ModSourceType.Local, id, id, true, priority, "1.0.0", new string('a', 64), null);
static ScanReport Report(IReadOnlyList<ModInstallation> mods, IReadOnlyList<string>? enabledOrder = null) => new("x", "0.71.44", new string('a', 64), null, enabledOrder ?? [], mods, new([], new Dictionary<string, IReadOnlyList<string>>(), []), [], [], ModPriorityOrder.UserFacingRule, DateTimeOffset.UnixEpoch);

static ModInstallation Mod(string id, string? folder = null, int priority = 0, string version = "0.1.0", ModSourceType source = ModSourceType.Local,
    IReadOnlyList<DependencySpec>? requires = null, IReadOnlyList<DependencySpec>? optional = null, IReadOnlyList<string>? incompatible = null,
    string? provider = null, int metadataMajor = 71)
{
    var manifest = new ChoirManifest(1, id, id, version, requires ?? [], optional ?? [], incompatible ?? [], [], null, null, "test", true, []);
    return new($"{source}:{id}:{Guid.NewGuid():N}", id, folder ?? id, source, folder ?? id, "C:\\private", new string('a', 64),
        new(id, "", version, metadataMajor, 44, "", "", true, []), 71, true, priority, manifest, provider, [], [], [], []);
}

static void WithJar(IReadOnlyDictionary<string, byte[]> entries, Action<string> action)
{
    WithDirectory(root =>
    {
        var path = Path.Combine(root, "test.jar");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var pair in entries) { var entry = archive.CreateEntry(pair.Key); using var stream = entry.Open(); stream.Write(pair.Value); }
        action(path);
    });
}

static void WithManyEntryJar(int count, Action<string> action)
{
    WithDirectory(root =>
    {
        var path = Path.Combine(root, "many.jar");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create)) for (var i = 0; i < count; i++) archive.CreateEntry($"e/{i}.txt");
        action(path);
    });
}

static void WithLocaleArchive(IReadOnlyDictionary<string, string> entries, Action<string> action)
{
    WithDirectory(root =>
    {
        var baseDirectory = Path.Combine(root, "base");
        Directory.CreateDirectory(baseDirectory);
        var path = Path.Combine(baseDirectory, "locale.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var pair in entries)
            {
                var entry = archive.CreateEntry(pair.Key);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(pair.Value);
            }
        action(root);
    });
}

static void WithTempFile(byte[] bytes, Action<string> action, string extension)
{
    WithDirectory(root => { var path = Path.Combine(root, "file" + extension); File.WriteAllBytes(path, bytes); action(path); });
}

static void WithDirectory(Action<string> action)
{
    var root = Path.Combine(Path.GetTempPath(), "ChoirLauncherTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { action(root); }
    finally { Directory.Delete(root, true); }
}

static void WithLaunchFiles(Action<SongsOfSyxEnvironment,string,string,string> action)
{
    WithDirectory(root =>
    {
        var local=Path.Combine(root,"mods");Directory.CreateDirectory(local);
        var settings=Path.Combine(root,"LauncherSettings.txt");File.WriteAllText(settings,RawSettings("Example"));
        var jar=Path.Combine(root,"SongsOfSyx.jar");WriteFakeGameJar(jar,70,12);
        var direct=Path.Combine(root,"SyxWithout.exe");File.WriteAllBytes(direct,[(byte)'M',(byte)'Z',7,8]);
        var official=Path.Combine(root,"SongsofSyx.exe");File.WriteAllBytes(official,[(byte)'M',(byte)'Z',11,12]);
        var environment=new SongsOfSyxEnvironment(settings,local,null,null,null,root,jar,[]);
        action(environment,Hashing.Sha256File(direct),Hashing.Sha256File(official),Hashing.Sha256File(jar));
    });
}

static void WriteFakeGameJar(string path, int major, int minor)
{
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    WriteEntry("META-INF/MANIFEST.MF", Encoding.UTF8.GetBytes("Manifest-Version: 1.0\nMain-Class: init.Main\n"));
    WriteEntry("init/Main.class", [0xCA,0xFE,0xBA,0xBE]);
    using var classBytes = new MemoryStream();
    U4(0xCAFEBABE); U2(0); U2(52); U2(11);
    Utf8("game/VERSION"); U1(7); U2(1);
    Utf8("java/lang/Object"); U1(7); U2(3);
    Utf8("VERSION_MAJOR"); Utf8("I"); Utf8("ConstantValue"); U1(3); U4(unchecked((uint)major));
    Utf8("VERSION_MINOR"); U1(3); U4(unchecked((uint)minor));
    U2(0x0031); U2(2); U2(4); U2(0); U2(2);
    Field(5,8); Field(9,10);
    U2(0); U2(0);
    WriteEntry("game/VERSION.class", classBytes.ToArray());

    void Field(ushort nameIndex, ushort valueIndex)
    {
        U2(0x0019); U2(nameIndex); U2(6); U2(1); U2(7); U4(2); U2(valueIndex);
    }
    void Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); U1(1); U2(checked((ushort)bytes.Length)); classBytes.Write(bytes);
    }
    void U1(byte value) => classBytes.WriteByte(value);
    void U2(ushort value) { classBytes.WriteByte((byte)(value >> 8)); classBytes.WriteByte((byte)value); }
    void U4(uint value) { classBytes.WriteByte((byte)(value >> 24)); classBytes.WriteByte((byte)(value >> 16)); classBytes.WriteByte((byte)(value >> 8)); classBytes.WriteByte((byte)value); }
    void WriteEntry(string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name); using var output = entry.Open(); output.Write(bytes);
    }
}

static void WithScan(Action<ScanReport, (string Path, string Before)> action)
{
    WithDirectory(root =>
    {
        var local = Path.Combine(root, "local"); var workshop = Path.Combine(root, "workshop"); var settings = Path.Combine(root, "LauncherSettings.txt");
        Directory.CreateDirectory(Path.Combine(local, "Example", "V71", "assets", "init", "resource")); Directory.CreateDirectory(workshop);
        File.WriteAllText(Path.Combine(local, "Example", "_Info.txt"), "VERSION: \"1.0.0\",\nGAME_VERSION_MAJOR: 71,\nNAME: \"Example\",");
        File.WriteAllText(Path.Combine(local, "Example", "V71", "assets", "init", "resource", "TEST.txt"), "VALUE: 1");
        File.WriteAllText(settings, RawSettings("Example"));
        var before = Hashing.Sha256File(settings);
        var report = new ModScanner().Scan(new(local, workshop, settings, null, 71, "0.71.44"));
        action(report, (settings, before));
    });
}

static void ScannerDiscoversWorkshop()
{
    WithDirectory(root =>
    {
        var local = Path.Combine(root, "local"); var workshop = Path.Combine(root, "workshop"); var settings = Path.Combine(root, "settings.txt");
        Directory.CreateDirectory(local); Directory.CreateDirectory(Path.Combine(workshop, "123", "V71"));
        File.WriteAllText(Path.Combine(workshop, "123", "_Info.txt"), "VERSION: \"1.0.0\",\nGAME_VERSION_MAJOR: 71,\nNAME: \"Workshop\",");
        File.WriteAllText(settings, RawSettings("123"));
        var report = new ModScanner().Scan(new(local, workshop, settings, null, 71, "0.71.44"));
        Equal(ModSourceType.Workshop, report.Mods.Single().Source);
    });
}

static void ScannerIdentityStable()
{
    WithScan((first, settings) =>
    {
        var root = Directory.GetParent(settings.Path)!.FullName;
        var second = new ModScanner().Scan(new(Path.Combine(root, "local"), Path.Combine(root, "workshop"), settings.Path, null, 71, "0.71.44"));
        Equal(first.Mods.Single().InstallationId, second.Mods.Single().InstallationId);
    });
}

static void WithDataPair(string a, string b, Action<IReadOnlyList<Conflict>> action)
{
    WithDirectory(root =>
    {
        var pa = Path.Combine(root, "a.txt"); var pb = Path.Combine(root, "b.txt");
        File.WriteAllText(pa, a); File.WriteAllText(pb, b);
        var ma = WithData(Mod("a"), pa, Hashing.Sha256File(pa)); var mb = WithData(Mod("b"), pb, Hashing.Sha256File(pb));
        action(ConflictAnalyzer.Analyze([ma, mb], ["a", "b"], 71));
    });
}

static void LiveSettingsUnchanged()
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "songsofsyx", "settings", "LauncherSettings.txt");
    if (!File.Exists(path)) return;
    var before = Hashing.Sha256File(path); _ = LauncherSettingsDocument.Parse(File.ReadAllText(path)); Equal(before, Hashing.Sha256File(path));
}

static void FrozenTreeUnchanged(string name)
{
    var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    var choir = Path.GetFullPath(Path.Combine(workspace, "..", "ChoirModdingFramework"));
    var baselines = Path.Combine(choir, "Baseline");
    if (!Directory.Exists(baselines)) return;
    var root = Directory.EnumerateDirectories(baselines, name, SearchOption.TopDirectoryOnly).FirstOrDefault();
    if (root is null) return;
    var before = TreeHash(root); var after = TreeHash(root); Equal(before, after);
}

static string TreeHash(string root)
{
    var lines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + "\0" + Hashing.Sha256File(path));
    return Hashing.Sha256(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
}

static void HasConflict(IReadOnlyList<Conflict> conflicts, string category) => True(conflicts.Any(x => x.Category == category));
static ModInstallation WithClasses(ModInstallation mod, params (string Name, string Hash)[] classes) => mod with { Jars = [new("test.jar", 1, new string('f', 64), true, classes.Select(x => new ArchiveClass(x.Name, x.Name.Replace('.', '/') + ".class", x.Hash)).ToArray(), [], [])] };
static ModInstallation WithJarHash(ModInstallation mod, string hash) => mod with { Jars = [new("test.jar", 1, string.Concat(Enumerable.Repeat(hash, 32))[..64], true, [], [], [])] };
static ModInstallation WithChoirApi(ModInstallation mod, string range) => mod with { Manifest = mod.Manifest! with { ChoirApiRange = range } };
static ModInstallation WithStableId(ModInstallation mod, string kind, string id) => mod with { StableIds = [new(kind, id, "fixture", Confidence.Proven)] };
static ModInstallation WithData(ModInstallation mod, string physical, string hash) => mod with { DataFiles = [new("V71/assets/init/resource/TEST.txt", hash, new FileInfo(physical).Length, "runtime-selected", physical)] };
static ManagerProfileEntry Entry(string id, bool enabled, string? sourceId = null) => new(id, id, ModSourceType.Local, sourceId ?? id, null, enabled, "1.0.0", null, null);
static ManagerProfileEntry EntryFrom(ModInstallation installation, bool enabled) => new(installation.LogicalModId, installation.LogicalModId, installation.Source, installation.SourceId, installation.InstallationId, enabled, installation.Manifest?.Version ?? installation.Metadata.Version, installation.ContentFingerprint, null);
static ManagerProfile MProfile(params ManagerProfileEntry[] entries) => new(ManagerProfileValidator.CurrentSchemaVersion, "manager-test", "Manager Test", "0.71.44", entries, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null);
static ProfileEditorSession Session(params string[] ids) => new(MProfile(ids.Select(id => Entry(id, true)).ToArray()));
static IReadOnlyList<string> Ids(ProfileEditorSession session) => session.Current.Mods.Select(x => x.EntryId).ToArray();
static Conflict TestConflict() => new("test-conflict", "class-collision", Severity.High, Confidence.Proven, ["A", "B"], "test.Type", "A", false, true, "Test conflict.", "Review.", ["fixture"]);

static void WithApplyFixture(Action<ApplyFixture> action)
{
    WithDirectory(root =>
    {
        var path = Path.Combine(root, "LauncherSettings.txt"); var backups = Path.Combine(root, "backups");
        File.WriteAllText(path, RawSettings("A"));
        var b = Mod("B"); var profile = MProfile(EntryFrom(b, true)); var resolved = ProfileResolver.Resolve(profile, [b]);
        var preview = ApplyPreviewService.Create(path, resolved, [], backups); var store = new ConfigurationBackupStore(backups);
        var writer = new OfficialConfigurationWriter(new SandboxConfigurationWriteGuard(root), new FakeProcesses(false), new NamedMutexApplyLockFactory(), store);
        action(new(root, path, backups, resolved, preview, store, writer));
    });
}

static void WithLauncherOptionsFixture(Action<string, LauncherOptionsPreview, LauncherOptionsWriter, ConfigurationBackupStore> action)
{
    WithDirectory(root =>
    {
        var path = Path.Combine(root, "LauncherSettings.txt");
        var backups = Path.Combine(root, "backups");
        File.WriteAllText(path, RawFullSettings("A"));
        var current = LauncherOptionsService.Load(path);
        var preview = LauncherOptionsService.CreatePreview(path, current with { VSync = false });
        var store = new ConfigurationBackupStore(backups);
        var writer = new LauncherOptionsWriter(
            new SandboxConfigurationWriteGuard(root),
            new FakeProcesses(false),
            new NamedMutexApplyLockFactory(),
            store);
        action(path, preview, writer, store);
    });
}

static void HeadlessWindowConstructs()
{
    if (Application.Current is null)
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
    WithDirectory(root =>
    {
        var window = new MainWindow(new MainWindowViewModel(root));
        True(window.Content is not null);
        window.Close();
    });
}

static void HeadlessWindowOpensBeforeProfilesLoad()
{
    if (Application.Current is null)
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
    WithDirectory(root =>
    {
        var window = new MainWindow(new MainWindowViewModel(root));
        window.Show();
        True(window.IsVisible);
        window.Close();
    });
}

static void DesktopToolbarIsSimplified() => WithHeadlessMainWindow(window =>
{
    var labels = window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string).Where(text => text is not null).ToArray();
    True(labels.Contains("Rescan Mods"));
    True(labels.Contains("Check Conflicts"));
    False(labels.Contains("↑ Up")); False(labels.Contains("↓ Down")); False(labels.Contains("⇈ Top")); False(labels.Contains("⇊ Bottom"));
    False(labels.Contains("Enable Selected")); False(labels.Contains("Disable Selected"));
});

static void DesktopToolbarGroupsToolsInReadingOrder() => WithHeadlessMainWindow(window =>
{
    var expected = new[] { "Game and launcher tools", "Mod analysis tools", "Profile edit tools" };
    var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
    var groups = window.GetVisualDescendants().OfType<Border>()
        .Where(border => expectedSet.Contains(AutomationProperties.GetName(border) ?? string.Empty))
        .ToArray();
    EqualSeq(expected, groups.Select(group => AutomationProperties.GetName(group)));
    True(groups.All(group => group.Background is SolidColorBrush && group.BorderBrush is SolidColorBrush));
    Equal(3, groups.Select(group => ((SolidColorBrush)group.BorderBrush!).Color).Distinct().Count());

    EqualSeq(
        new[] { "Configure the official Songs of Syx v71.44 launcher settings", "Show the installed game version, hardware, and Songs of Syx folders", "Choose Songs of Syx language" },
        groups[0].GetVisualDescendants().OfType<Button>().Select(button => AutomationProperties.GetName(button)));
    EqualSeq(
        new[] { "Rescan local and Workshop mod folders (F5)", "Cancel the current mod scan", "Analyze enabled mods and show the conflict report", "Preview dependency-aware order" },
        groups[1].GetVisualDescendants().OfType<Button>().Select(button => AutomationProperties.GetName(button)));
    EqualSeq(
        new[] { "Save profile (Ctrl+S)", "Undo (Ctrl+Z)", "Redo (Ctrl+Y)" },
        groups[2].GetVisualDescendants().OfType<Button>().Select(button => AutomationProperties.GetName(button)));
});

static void DesktopUsesSongsOfSyxWindowIcon() => WithHeadlessMainWindow(window =>
{
    True(window.Icon is not null);
    True(Avalonia.Platform.AssetLoader.Exists(new Uri("avares://ChoirLauncher/Assets/SongsOfSyxIcon64.png")));
});

static void SettingsDisplayColumnContainsControls()
{
    var settings = typeof(MainWindow).Assembly.GetType("ChoirLauncher.Desktop.GameSettingsWindow")
        ?? throw new InvalidOperationException("Game settings window type was not found.");
    static double Constant(Type type, string name) => Convert.ToDouble(type.GetField(name,
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.GetRawConstantValue());
    var column = Constant(settings, "DisplayCoreColumnWidth");
    var label = Constant(settings, "DisplayLabelWidth");
    var spacing = Constant(settings, "SettingColumnSpacing");
    var control = Constant(settings, "ScreenModeControlWidth");
    True(column >= label + spacing + control);
}

static void DesktopLaunchIsActionable() => WithHeadlessMainWindow(window =>
{
    var launch = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Launch Songs of Syx");
    True(launch.IsEnabled); True(launch.MinHeight >= 90); True(launch.MinWidth >= 400); True(ToolTip.GetTip(launch) is string text && text.Contains("verified direct-game route", StringComparison.OrdinalIgnoreCase));
});

static void DesktopLaunchSelectorDefaultsToApplyProfile() => WithHeadlessMainWindow(window =>
{
    var selector = window.GetVisualDescendants().OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "Default launch action");
    var selected = selector.SelectedItem ?? throw new InvalidOperationException("Default launch action was not selected.");
    var value = selected.GetType().GetProperty("Value")?.GetValue(selected);
    Equal(LauncherLaunchAction.ApplyProfileAndLaunch, value);
    True(selector.Width >= 240); True(selector.Height >= 40);
});

static void DesktopLaunchUsesVanillaLauncherArt() => WithHeadlessMainWindow(window =>
{
    var launch = window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Launch Songs of Syx");
    True(launch.Content is StackPanel);
    var images = launch.GetVisualDescendants().OfType<Image>().ToArray();
    Equal(2, images.Length);
    True(images.All(image => image.Source is not null));
    var crops = images.Select(image => image.Source).OfType<Avalonia.Media.Imaging.CroppedBitmap>().ToArray();
    EqualSeq(new[] { new PixelRect(6, 76, 384, 64), new PixelRect(6, 184, 464, 16) }, crops.Select(crop => crop.SourceRect));
    True(Avalonia.Platform.AssetLoader.Exists(new Uri("avares://ChoirLauncher/Assets/VanillaLauncherSprites.png")));
});

static void DesktopUsesOwnerBackground() => WithHeadlessMainWindow(window =>
{
    var background = window.GetVisualDescendants().OfType<Image>().Single(image => image.Classes.Contains("owner-city-background"));
    True(background.Source is not null);
    Equal(Avalonia.Media.Stretch.UniformToFill, background.Stretch);
    False(background.IsHitTestVisible);
    True(background.Opacity is >= 0.5 and <= 0.7);
    True(Avalonia.Platform.AssetLoader.Exists(new Uri("avares://ChoirLauncher/Assets/OwnerLauncherBackground.png")));
});

static void DesktopExposesGameLauncherFeatures() => WithHeadlessMainWindow(window =>
{
    var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
    True(buttons.Any(button => Equals(button.Content, "Settings")));
    True(buttons.Any(button => Equals(button.Content, "Info")));
    var language = buttons.Single(button => AutomationProperties.GetName(button) == "Choose Songs of Syx language");
    True(language.Content is StackPanel);
    var icon = language.GetVisualDescendants().OfType<Image>().Single();
    var crop = icon.Source as Avalonia.Media.Imaging.CroppedBitmap;
    True(crop is not null);
    Equal(new PixelRect(6, 258, 24, 24), crop!.SourceRect);
});

static void DesktopModListRequestsScrolling() => WithHeadlessMainWindow(window =>
{
    var modList = window.GetVisualDescendants().OfType<ListBox>()
        .Single(control => ScrollViewer.GetVerticalScrollBarVisibility(control) == ScrollBarVisibility.Auto);
    Equal(SelectionMode.Multiple, modList.SelectionMode);
});

static void DesktopProfileManagementEntryPoint() => WithHeadlessMainWindow(window =>
{
    var manage = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), "Manage profiles"));
    var selector = window.GetVisualDescendants().OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "Active profile");
    Equal(42d, manage.Width); Equal(42d, manage.Height); Equal(42d, selector.Height); Equal(new Thickness(0), manage.Margin);
    True(manage.Content is TextBlock { Text: "•••" });
});

static void DesktopModColumnsAreResizable() => WithHeadlessMainWindow(window =>
{
    var splitters = window.GetVisualDescendants().OfType<GridSplitter>().ToArray();
    Equal(9, splitters.Length);
    True(splitters.All(splitter => Equals(ToolTip.GetTip(splitter), "Drag to resize adjacent columns")));
});

static void DesktopRestoredColumnsStayContained()
{
    if (Application.Current is null)
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
    WithDirectory(root =>
    {
        var paths = ManagerStoragePaths.Resolve(root);
        new DesktopPreferencesStore(paths).Save(new DesktopPreferences(1, null, 1200, 760, null, null, false,
            Enumerable.Repeat(1000d, 10).ToArray()));
        var window = new MainWindow(new MainWindowViewModel(root));
        try
        {
            window.Show(); window.UpdateLayout();
            var viewport = window.GetVisualDescendants().OfType<Border>()
                .Single(control => AutomationProperties.GetName(control) == "Mod list viewport");
            var table = window.GetVisualDescendants().OfType<Grid>()
                .Single(control => AutomationProperties.GetName(control) == "Resizable mod table");
            var header = window.GetVisualDescendants().OfType<Grid>()
                .Single(control => control.Children.OfType<GridSplitter>().Count() == 9);
            True(viewport.ClipToBounds); True(table.ClipToBounds);
            True(header.ColumnDefinitions.Sum(column => column.ActualWidth) <= header.Bounds.Width + 1);
        }
        finally { window.Close(); }
    });
}

static void DesktopProfileActionsAreExplicit() => WithHeadlessMainWindow(window =>
{
    var labels = window.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string).Where(text => text is not null).ToArray();
    True(labels.Contains("Choose Installed Copy"));
    True(labels.Contains("Edit Profile Notes"));
    True(labels.Contains("Add Removed Mods Back"));
    True(labels.Contains("Remove from Profile"));
    False(labels.Contains("Relink"));
    False(labels.Contains("Remove Entry"));
});

static void DesktopModNamesInheritForeground()
{
    var factory = typeof(MainWindow).GetMethod("Text", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Row text factory was not found.");
    var block = (TextBlock)(factory.Invoke(null, ["ExpandedProduction", null, null])
        ?? throw new InvalidOperationException("Row text factory returned null."));
    Equal("ExpandedProduction", block.Text);
    False(block.IsSet(TextBlock.ForegroundProperty));
}

static void WithHeadlessMainWindow(Action<MainWindow> action)
{
    if (Application.Current is null)
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
    WithDirectory(root =>
    {
        var window = new MainWindow(new MainWindowViewModel(root));
        try { window.Show(); action(window); }
        finally { window.Close(); }
    });
}

static void LargeProfilePerformance()
{
    var entries = Enumerable.Range(0, 5000).Select(i => Entry("M" + i, i % 3 != 0)).ToArray();
    var session = new ProfileEditorSession(MProfile(entries));
    var selection = Enumerable.Range(0, 1000).Where(i => i % 2 == 0).Select(i => "M" + i).ToArray();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    True(session.MoveToBottom(selection)); timer.Stop();
    Equal(5000, session.Current.Mods.Count); Equal(selection.Length, session.Current.Mods.TakeLast(selection.Length).Count());
    True(timer.Elapsed < TimeSpan.FromSeconds(5));
}
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
static void Null(object? value) { if (value is not null) throw new InvalidOperationException("Expected null."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
static void EqualSeq<T>(IEnumerable<T> expected, IEnumerable<T> actual) { if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"Expected [{string.Join(',', expected)}]; actual [{string.Join(',', actual)}]."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }

sealed class TestSuite
{
    private readonly List<(string Name, Action Test)> tests = [];
    public void Add(string name, Action test) => tests.Add((name, test));
    public int Run()
    {
        var failures = new List<string>();
        foreach (var item in tests)
        {
            try { item.Test(); Console.WriteLine($"PASS {item.Name}"); }
            catch (Exception ex) { failures.Add($"FAIL {item.Name}: {ex.Message}"); Console.WriteLine(failures[^1]); }
        }
        Console.WriteLine($"RESULT total={tests.Count} passed={tests.Count - failures.Count} failed={failures.Count}");
        return failures.Count == 0 ? 0 : 1;
    }
}

sealed record ApplyFixture(string Root, string Path, string Backups, ResolvedProfile Resolved, ApplyPreview Preview, ConfigurationBackupStore Store, OfficialConfigurationWriter Writer);

sealed class FakeProcesses(bool blocked) : IProcessInspector
{
    public IReadOnlyList<BlockingProcess> FindBlockingProcesses() => blocked ? [new(42, "SongsofSyx", "fixture")] : [];
}

sealed class FakeLockFactory(bool acquired) : IApplyLockFactory
{
    public IApplyLock TryAcquire(string targetPath, TimeSpan timeout) => new FakeLock(acquired);
    private sealed class FakeLock(bool acquired) : IApplyLock { public bool Acquired { get; } = acquired; public void Dispose() { } }
}

sealed class FakeLaunchTargetResolver : IGameLaunchTargetResolver
{
    public GameLaunchTarget Resolve(SongsOfSyxEnvironment environment, GameLaunchRoute route) =>
        new(route, route == GameLaunchRoute.DirectGame ? "fixture direct game" : "fixture official launcher", Path.Combine(environment.GameRoot!, "fixture.exe"), environment.GameRoot!, new string('a',64), new string('b',64), "0.70.12", false, false, []);
}

sealed class FakeGameStarter : IGameProcessStarter
{
    public int Count { get; private set; }
    public int Start(GameLaunchTarget target) { Count++; return 4242; }
}

sealed class ThrowAt(string phase) : IApplyFaultInjector
{
    public void At(string current) { if (current == phase) throw new IOException("Injected failure at " + phase); }
}
