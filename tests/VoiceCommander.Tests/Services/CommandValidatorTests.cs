using VoiceCommander.Core.Models;
using VoiceCommander.Core.Services;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Services;

public class CommandValidatorTests
{
    [Fact]
    public void Valid_command_has_no_errors() =>
        Assert.Empty(CommandValidator.Validate(Make.Command("a", new[] { "open notepad" }, "app.open")));

    [Fact]
    public void Blank_name_is_rejected()
    {
        var c = Make.Command("a", new[] { "x y" });
        c.Name = "  ";
        Assert.Contains("validation.name", CommandValidator.Validate(c));
    }

    [Fact]
    public void Missing_or_blank_phrases_are_rejected()
    {
        var c = Make.Command("a", Array.Empty<string>());
        Assert.Contains("validation.phrases", CommandValidator.Validate(c));
        c.Phrases = new List<string> { "   ", "?!" };
        Assert.Contains("validation.phrases", CommandValidator.Validate(c));
    }

    [Fact]
    public void Missing_actions_are_rejected()
    {
        var c = Make.Command("a", new[] { "go" });
        c.Actions.Clear();
        Assert.Contains("validation.actions", CommandValidator.Validate(c));
    }

    [Fact]
    public void Blank_action_type_is_rejected()
    {
        var c = Make.Command("a", new[] { "go" });
        c.Actions = new List<ActionStep> { new("") };
        Assert.Contains("validation.actiontype", CommandValidator.Validate(c));
    }

    [Fact]
    public void Unbalanced_braces_are_rejected()
    {
        var c = Make.Command("a", new[] { "volume {number" });
        Assert.Contains("validation.braces", CommandValidator.Validate(c));
    }
}
