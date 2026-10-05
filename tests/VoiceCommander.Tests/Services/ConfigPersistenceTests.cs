using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Services;

public class ConfigPersistenceTests
{
    private sealed class Sample
    {
        public string Name { get; set; } = "";
        public List<int> Numbers { get; set; } = new();
    }

    [Fact]
    public void JsonStore_round_trips_and_creates_missing_directories()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Root, "deep", "nested", "s.json");
        JsonStore.Save(path, new Sample { Name = "اختبار", Numbers = { 1, 2, 3 } });
        var back = JsonStore.Load<Sample>(path)!;
        Assert.Equal("اختبار", back.Name);
        Assert.Equal(new[] { 1, 2, 3 }, back.Numbers);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void JsonStore_returns_null_when_file_is_missing()
    {
        using var sb = new Sandbox();
        Assert.Null(JsonStore.Load<Sample>(Path.Combine(sb.Root, "nope.json")));
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_load_returns_null()
    {
        using var sb = new Sandbox();
        var path = Path.Combine(sb.Root, "bad.json");
        File.WriteAllText(path, "{ this is not json");
        Assert.Null(JsonStore.Load<Sample>(path));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(sb.Root, "bad.json.corrupt-*"));
    }

    [Fact]
    public void Settings_default_when_no_file_and_persist_across_instances()
    {
        using var sb = new Sandbox();
        var a = new SettingsService(new NullLog());
        Assert.Equal("en", a.Current.Language);
        Assert.True(a.Current.ConfirmDangerousActions);
        a.Current.Language = "ar";
        a.Current.CommandCooldownMs = 2500;
        a.Save();

        var b = new SettingsService(new NullLog());
        Assert.Equal("ar", b.Current.Language);
        Assert.Equal(2500, b.Current.CommandCooldownMs);
    }

    [Fact]
    public void Settings_are_sanitized_on_save()
    {
        using var sb = new Sandbox();
        var s = new SettingsService(new NullLog());
        s.Current.ConfidenceThreshold = 7;
        s.Current.SimilarityTolerance = 3;
        s.Current.CommandCooldownMs = -5;
        s.Current.CommandTimeoutSeconds = 0;
        s.Current.Language = "";
        s.Current.DisabledPacks = null!;
        s.Save();

        var c = s.Current;
        Assert.InRange(c.ConfidenceThreshold, 0, 1);
        Assert.InRange(c.SimilarityTolerance, 0, 0.5);
        Assert.True(c.CommandCooldownMs >= 0);
        Assert.True(c.CommandTimeoutSeconds >= 1);
        Assert.Equal("en", c.Language);
        Assert.NotNull(c.DisabledPacks);
    }

    [Fact]
    public void Dangerous_actions_are_disabled_by_default()
    {
        var s = new AppSettings();
        Assert.True(s.DisableShutdownCommands);
        Assert.True(s.DisableRestartCommands);
        Assert.True(s.DisableSignOutCommands);
        Assert.True(s.DisableSleepCommands);
        Assert.True(s.ConfirmDangerousActions);
        Assert.Equal(1500, s.CommandCooldownMs);
        Assert.Equal("Ctrl+Space", s.PushToTalkHotkey);
    }

    [Fact]
    public void Commands_repository_seeds_built_ins_on_first_run()
    {
        using var sb = new Sandbox();
        var repo = new CommandRepository(new NullLog(), new MemorySettings());
        Assert.NotEmpty(repo.Commands);
        Assert.All(repo.Commands, c => Assert.True(c.IsBuiltIn));
        Assert.Empty(repo.Commands.SelectMany(c => CommandValidator.Validate(c)));
        Assert.True(File.Exists(AppPaths.CommandsFile));
    }

    [Fact]
    public void Built_in_command_ids_are_unique_and_every_action_type_is_known()
    {
        using var sb = new Sandbox();
        var repo = new CommandRepository(new NullLog(), new MemorySettings());
        Assert.Equal(repo.Commands.Count, repo.Commands.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Deleted_built_in_stays_deleted_after_reload()
    {
        using var sb = new Sandbox();
        var settings = new MemorySettings();
        var repo = new CommandRepository(new NullLog(), settings);
        var victim = repo.Commands.First();
        Assert.True(repo.Remove(victim.Id));
        Assert.Null(repo.FindById(victim.Id));

        var again = new CommandRepository(new NullLog(), settings);
        Assert.Null(again.FindById(victim.Id));
        Assert.False(again.Remove(victim.Id));

        again.RestoreBuiltIns();
        Assert.NotNull(again.FindById(victim.Id));
    }

    [Fact]
    public void Custom_commands_persist_and_can_be_edited()
    {
        using var sb = new Sandbox();
        var settings = new MemorySettings();
        var repo = new CommandRepository(new NullLog(), settings);
        var cmd = Make.Command("custom.gaming", new[] { "start gaming", "ابدأ اللعب" }, "app.open", "wait");
        cmd.Name = "Start Gaming";
        repo.AddOrUpdate(cmd);

        var again = new CommandRepository(new NullLog(), settings);
        var loaded = again.FindById("custom.gaming")!;
        Assert.Equal("Start Gaming", loaded.Name);
        Assert.Equal(new[] { "app.open", "wait" }, loaded.Actions.Select(a => a.Type));
        Assert.Contains("ابدأ اللعب", loaded.Phrases);

        loaded.Name = "Renamed";
        again.AddOrUpdate(loaded);
        Assert.Equal("Renamed", new CommandRepository(new NullLog(), settings).FindById("custom.gaming")!.Name);
        Assert.Equal(1, again.Commands.Count(c => c.Id == "custom.gaming"));
    }

    [Fact]
    public void Duplicate_ids_in_the_file_are_collapsed_on_load()
    {
        using var sb = new Sandbox();
        var file = new CommandStoreFile
        {
            SeedVersion = int.MaxValue,
            Commands = new List<VoiceCommand>
            {
                Make.Command("dup", new[] { "one" }),
                Make.Command("dup", new[] { "two" }),
            }
        };
        JsonStore.Save(AppPaths.CommandsFile, file);
        var repo = new CommandRepository(new NullLog(), new MemorySettings());
        Assert.Single(repo.Commands);
    }

    [Fact]
    public void Disabled_pack_commands_are_not_active()
    {
        using var sb = new Sandbox();
        var settings = new MemorySettings();
        var repo = new CommandRepository(new NullLog(), settings);
        var pack = repo.Commands.Where(c => c.PackId != null).Select(c => c.PackId!).Distinct().First();
        var before = repo.ActiveCommands.Count(c => c.PackId == pack);
        Assert.True(before > 0);

        repo.SetPackEnabled(pack, false);
        Assert.DoesNotContain(repo.ActiveCommands, c => c.PackId == pack);
        Assert.False(repo.IsPackEnabled(pack));

        repo.SetPackEnabled(pack, true);
        Assert.Equal(before, repo.ActiveCommands.Count(c => c.PackId == pack));
    }

    [Fact]
    public void Application_registry_seeds_defaults_and_persists_edits()
    {
        using var sb = new Sandbox();
        var reg = new AppRegistry(new NullLog());
        Assert.NotNull(reg.FindById("app.notepad"));
        Assert.NotNull(reg.FindById("app.discord"));

        var mine = Make.App("app.mine", "My Tool", "my tool", "tool");
        mine.ExecutablePath = @"C:\Tools\mytool.exe";
        reg.AddOrUpdate(mine);

        var again = new AppRegistry(new NullLog());
        Assert.Equal(@"C:\Tools\mytool.exe", again.FindById("app.mine")!.ExecutablePath);
        Assert.Equal("app.mine", again.FindByName("tool")!.Id);

        Assert.True(again.Remove("app.mine"));
        Assert.Null(new AppRegistry(new NullLog()).FindById("app.mine"));
    }

    [Fact]
    public void History_persists_and_skips_damaged_lines()
    {
        using var sb = new Sandbox();
        var h = new HistoryService(new NullLog());
        h.Add(new HistoryEntry { RecognizedText = "open notepad", CommandId = "a", Outcome = ExecutionOutcome.Success });
        h.Add(new HistoryEntry { RecognizedText = "volume down", CommandId = "b", Outcome = ExecutionOutcome.Failed });
        File.AppendAllText(AppPaths.HistoryFile, "{{{ garbage line\n");
        h.Add(new HistoryEntry { RecognizedText = "third", CommandId = "c", Outcome = ExecutionOutcome.NoMatch });

        var again = new HistoryService(new NullLog());
        Assert.Equal(3, again.Entries.Count);
        Assert.Contains(again.Entries, e => e.RecognizedText == "volume down");

        again.Clear();
        Assert.Empty(new HistoryService(new NullLog()).Entries);
    }

    [Fact]
    public void History_is_capped()
    {
        using var sb = new Sandbox();
        var h = new HistoryService(new NullLog());
        for (int i = 0; i < 520; i++) h.Add(new HistoryEntry { RecognizedText = "t" + i });
        Assert.True(h.Entries.Count <= 500);
        Assert.Contains(h.Entries, e => e.RecognizedText == "t519");
        Assert.DoesNotContain(h.Entries, e => e.RecognizedText == "t0");
    }
}
