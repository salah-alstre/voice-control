using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using Xunit.Abstractions;

namespace VoiceCommander.Tests.Matching;

/// <summary>Arabic recognition-error tolerance against the REAL built-in commands and applications.</summary>
public class ArabicMatcherTests
{
    private readonly ITestOutputHelper _out;
    private readonly CommandMatcher _m = new();
    private readonly IReadOnlyList<VoiceCommand> _commands = BuiltInCommands.Create();
    private readonly IReadOnlyList<AppDefinition> _apps = BuiltInApps.Create();

    public ArabicMatcherTests(ITestOutputHelper output) => _out = output;

    private MatchEvaluation Eval(string text)
    {
        var e = _m.Evaluate(text, _commands, _apps);
        _out.WriteLine($"{text} => {e.Diagnostics.Summary}");
        return e;
    }

    // ---- the required voice commands, spoken correctly ----
    [Theory]
    [InlineData("افتح المفكرة", "apps.open", "app.notepad")]
    [InlineData("شغل المفكرة", "apps.open", "app.notepad")]
    [InlineData("افتح نوت باد", "apps.open", "app.notepad")]
    [InlineData("افتح الحاسبة", "apps.open", "app.calculator")]
    [InlineData("شغل الحاسبة", "apps.open", "app.calculator")]
    [InlineData("افتح ديسكورد", "discord.open", null)]
    [InlineData("شغل ديسكورد", "discord.open", null)]
    [InlineData("سكر ديسكورد", "discord.close", null)]
    [InlineData("اغلق ديسكورد", "discord.close", null)]
    [InlineData("ارفع الصوت", "audio.up", null)]
    [InlineData("علي الصوت", "audio.up", null)]
    [InlineData("زيد الصوت", "audio.up", null)]
    [InlineData("وطي الصوت", "audio.down", null)]
    [InlineData("نزل الصوت", "audio.down", null)]
    [InlineData("خفض الصوت", "audio.down", null)]
    [InlineData("قلل الصوت", "audio.down", null)]
    [InlineData("اكتم الصوت", "audio.mute", null)]
    [InlineData("سكر الصوت", "audio.mute", null)]
    [InlineData("صور الشاشة", "windows.screenshot", null)]
    [InlineData("خذ لقطة شاشة", "windows.screenshot", null)]
    [InlineData("اعمل سكرين شوت", "windows.screenshot", null)]
    public void Required_phrases_match_exactly(string text, string commandId, string? appId)
    {
        var e = Eval(text);
        Assert.NotNull(e.Result);
        Assert.True(e.Result!.IsExact);
        Assert.Equal(MatchDecision.Exact, e.Diagnostics.Decision);
        Assert.Equal(commandId, e.Result.Command.Id);
        if (appId != null) Assert.Equal(appId, e.Result.App?.Id);
    }

    [Fact]
    public void Exact_match_with_diacritics_and_hamza_variants()
    {
        var e = Eval("أَفْتَحْ المُفَكِّرَة");
        Assert.True(e.Result!.IsExact);
        Assert.Equal("app.notepad", e.Result.App!.Id);
    }

    // ---- the acceptance test and other realistic Vosk mistakes ----
    [Theory]
    [InlineData("افتتاح المفكرة", "apps.open", "app.notepad")]   // THE acceptance case
    [InlineData("افتتح المفكرة", "apps.open", "app.notepad")]
    [InlineData("افتتح المفكر", "apps.open", "app.notepad")]
    [InlineData("افتتاح الحاسبة", "apps.open", "app.calculator")]
    [InlineData("افتتح الحاسبه", "apps.open", "app.calculator")]
    [InlineData("افتتح ستيم", "apps.open", "app.steam")]
    [InlineData("افتتاح الاعدادات", "settings.home", null)]
    public void Damaged_verb_with_clear_target_resolves(string text, string commandId, string? appId)
    {
        var e = Eval(text);
        Assert.NotNull(e.Result);
        Assert.False(e.Result!.IsExact);
        Assert.Equal(MatchDecision.Fuzzy, e.Diagnostics.Decision);
        Assert.Equal(commandId, e.Result.Command.Id);
        if (appId != null) Assert.Equal(appId, e.Result.App?.Id);
    }

    // ---- must never execute ----
    [Theory]
    [InlineData("فتح")]
    [InlineData("افتتح")]
    [InlineData("لدى")]
    [InlineData("لاقت")]
    [InlineData("استح ستين")]
    [InlineData("افتح العدد")]
    [InlineData("مرحبا كيف حالك اليوم")]
    [InlineData("الصوت")]
    [InlineData("المفكرة")]   // a bare target without any verb is too weak to act on
    public void Garbage_or_unclear_text_is_not_executed(string text)
    {
        var e = Eval(text);
        Assert.Null(e.Result);
    }

    [Theory]
    [InlineData("اطفي الكمبيوتر")]
    [InlineData("اغلاق الكومبيوتر")]
    [InlineData("اعادة تشغيل الكمبيوتر")]
    [InlineData("سجل الخروج")]
    [InlineData("نيم الكومبيوتر")]
    public void Dangerous_commands_never_win_a_fuzzy_match(string text)
    {
        var e = Eval(text);
        Assert.True(e.Result == null || e.Result.IsExact || !CommandMatcher.IsDangerous(e.Result.Command));
        Assert.True(e.Result == null || !CommandMatcher.IsDangerous(e.Result.Command), "power commands are disabled by default and must not run");
    }

    [Fact]
    public void Diagnostics_report_best_and_second_best()
    {
        var e = Eval("افتتاح المفكرة");
        var d = e.Diagnostics;
        Assert.Equal("افتتاح المفكرة", d.OriginalText);
        Assert.Equal("افتتاح المفكره", d.NormalizedText);
        Assert.NotNull(d.BestCommandId);
        Assert.True(d.BestScore > d.SecondBestScore);
    }
}
