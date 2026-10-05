using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Services;

public class ImportExportTests
{
    private static (ImportExportService svc, CommandRepository cmds, AppRegistry apps) Build()
    {
        var log = new NullLog();
        var cmds = new CommandRepository(log, new MemorySettings());
        var apps = new AppRegistry(log);
        return (new ImportExportService(cmds, apps, log), cmds, apps);
    }

    [Fact]
    public void Export_then_import_into_a_fresh_profile_round_trips_everything()
    {
        string file;
        VoiceCommand custom;
        using (var sb = new Sandbox())
        {
            var (svc, cmds, apps) = Build();
            custom = Make.Command("custom.gaming", new[] { "start gaming", "ابدأ اللعب" }, "app.open", "wait");
            custom.Actions[1] = new ActionStep("wait", ("seconds", "2"));
            cmds.AddOrUpdate(custom);
            var app = Make.App("app.mine", "My Tool", "tool");
            app.ExecutablePath = @"C:\Tools\mytool.exe";
            apps.AddOrUpdate(app);

            file = Path.Combine(Path.GetTempPath(), "vc-export-" + Guid.NewGuid().ToString("N") + ".voicecommander");
            svc.Export(file);
        }

        try
        {
            using var sb2 = new Sandbox();
            var (svc2, cmds2, apps2) = Build();
            cmds2.ReplaceAll(Array.Empty<VoiceCommand>());
            var preview = svc2.Preview(file);
            Assert.Contains(preview.Commands, c => c.Id == "custom.gaming");
            Assert.Equal(0, preview.Skipped);

            var result = svc2.Apply(preview, ImportMode.Merge);
            Assert.Equal(preview.Commands.Count, result.Commands);

            var loaded = cmds2.FindById("custom.gaming")!;
            Assert.Equal(new[] { "start gaming", "ابدأ اللعب" }, loaded.Phrases);
            Assert.Equal(new[] { "app.open", "wait" }, loaded.Actions.Select(a => a.Type));
            Assert.Equal("2", loaded.Actions[1].Get("seconds", ""));
            Assert.Equal(@"C:\Tools\mytool.exe", apps2.FindById("app.mine")!.ExecutablePath);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Export_can_exclude_applications()
    {
        using var sb = new Sandbox();
        var (svc, _, _) = Build();
        var file = Path.Combine(sb.Root, "x.voicecommander");
        svc.Export(file, commands: true, applications: false);
        var p = svc.Preview(file);
        Assert.NotEmpty(p.Commands);
        Assert.Empty(p.Applications);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"Format\":\"something-else\",\"Version\":1}")]
    [InlineData("[]")]
    public void Invalid_files_are_rejected_without_touching_data(string content)
    {
        using var sb = new Sandbox();
        var (svc, cmds, _) = Build();
        var before = cmds.Commands.Count;
        var file = Path.Combine(sb.Root, "bad.voicecommander");
        File.WriteAllText(file, content);
        Assert.Throws<InvalidDataException>(() => svc.Preview(file));
        Assert.Equal(before, cmds.Commands.Count);
    }

    [Fact]
    public void Missing_file_is_reported_as_invalid()
    {
        using var sb = new Sandbox();
        var (svc, _, _) = Build();
        Assert.Throws<InvalidDataException>(() => svc.Preview(Path.Combine(sb.Root, "missing.voicecommander")));
    }

    [Fact]
    public void Invalid_commands_and_apps_are_skipped_and_counted()
    {
        using var sb = new Sandbox();
        var (svc, _, _) = Build();
        var good = Make.Command("good", new[] { "do good" });
        var noPhrases = Make.Command("bad1", Array.Empty<string>());
        var noActions = Make.Command("bad2", new[] { "x y" });
        noActions.Actions.Clear();
        var badApp = new AppDefinition { Id = "a", Name = "", ExecutablePath = "" };

        var file = Path.Combine(sb.Root, "mixed.voicecommander");
        Core.Infrastructure.JsonStore.Save(file, new ExchangeFile
        {
            Commands = { good, noPhrases, noActions },
            Applications = { badApp },
        });

        var p = svc.Preview(file);
        Assert.Single(p.Commands);
        Assert.Equal("good", p.Commands[0].Id);
        Assert.Equal(3, p.Skipped);
    }

    [Fact]
    public void Blank_ids_get_a_fresh_id()
    {
        using var sb = new Sandbox();
        var (svc, _, _) = Build();
        var c = Make.Command("", new[] { "do it" });
        c.Name = "No id";
        var file = Path.Combine(sb.Root, "noid.voicecommander");
        Core.Infrastructure.JsonStore.Save(file, new ExchangeFile { Commands = { c } });
        var p = svc.Preview(file);
        Assert.False(string.IsNullOrWhiteSpace(p.Commands.Single().Id));
    }

    [Fact]
    public void Merge_keeps_existing_commands_and_updates_matching_ids()
    {
        using var sb = new Sandbox();
        var (svc, cmds, _) = Build();
        var existing = cmds.Commands.Count;
        var edited = cmds.Commands.First().Clone();
        edited.Name = "Edited by import";
        var added = Make.Command("brand.new", new[] { "brand new" });

        var preview = new ImportPreview();
        preview.Commands.Add(edited);
        preview.Commands.Add(added);
        svc.Apply(preview, ImportMode.Merge);

        Assert.Equal(existing + 1, cmds.Commands.Count);
        Assert.Equal("Edited by import", cmds.FindById(edited.Id)!.Name);
    }

    [Fact]
    public void Replace_swaps_the_whole_set_but_ignores_empty_sections()
    {
        using var sb = new Sandbox();
        var (svc, cmds, apps) = Build();
        var appCount = apps.Apps.Count;

        var preview = new ImportPreview();
        preview.Commands.Add(Make.Command("only.one", new[] { "only one" }));
        svc.Apply(preview, ImportMode.Replace);

        Assert.Equal("only.one", Assert.Single(cmds.Commands).Id);
        Assert.Equal(appCount, apps.Apps.Count); // no apps in preview -> untouched
    }
}
