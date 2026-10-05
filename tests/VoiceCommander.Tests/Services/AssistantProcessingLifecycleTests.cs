using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;
using VoiceCommander.Tests.Support;

namespace VoiceCommander.Tests.Services;

/// <summary>The state the UI (dashboard + floating assistant) follows while an utterance-based engine (Whisper) works.</summary>
public sealed class AssistantProcessingLifecycleTests : IDisposable
{
    private readonly FakeListening _listening = new();
    private readonly FakePipeline _pipeline = new();
    private readonly AssistantStateService _svc;
    private readonly List<AssistantPhase> _phases = new();

    public AssistantProcessingLifecycleTests()
    {
        _svc = new AssistantStateService(_listening, _pipeline, new FakeCommands(), new FakeApps(), new FakeIcons(), new NullLog());
        _svc.Changed += s => { lock (_phases) _phases.Add(s.Phase); };
        _svc.Start();
    }

    public void Dispose() { (_svc as IDisposable)?.Dispose(); }

    private AssistantPhase Phase => _svc.Current.Phase;

    private static ExecutionReport Report(bool ok) =>
        new(ok ? ExecutionOutcome.Success : ExecutionOutcome.Failed, ok ? "Done" : "Failed", null, "Open Notepad", 12, 1, 1);

    [Fact]
    public void Starts_ready()
    {
        Assert.Equal(AssistantPhase.Ready, Phase);
    }

    [Fact]
    public void Full_whisper_lifecycle_is_listening_processing_heard_executing_done_and_never_ready_in_between()
    {
        _listening.PushToTalkDown();
        Assert.Equal(AssistantPhase.Listening, Phase);

        // Key released: the microphone stops, but Whisper is still transcribing.
        _listening.SetProcessing(true);
        _listening.PushToTalkUp();
        Assert.Equal(AssistantPhase.Processing, Phase);

        _pipeline.Raise(new PipelineEvent(PipelineStage.Heard, "افتح المفكرة"));
        Assert.Equal(AssistantPhase.Processing, Phase);
        Assert.Equal("افتح المفكرة", _svc.Current.HeardText);

        _listening.SetProcessing(false);
        Assert.Equal(AssistantPhase.Processing, Phase);   // the engine finishing alone must not flash Ready

        _pipeline.Raise(new PipelineEvent(PipelineStage.Matched, "افتح المفكرة", "Open Notepad"));
        Assert.Equal(AssistantPhase.Executing, Phase);

        _pipeline.Raise(new PipelineEvent(PipelineStage.Executed, "افتح المفكرة", "Open Notepad", Report: Report(true)));
        Assert.Equal(AssistantPhase.Success, Phase);

        List<AssistantPhase> seen;
        lock (_phases) seen = _phases.ToList();
        Assert.DoesNotContain(AssistantPhase.Ready, seen);
        Assert.Equal(AssistantPhase.Listening, seen.First());
    }

    [Fact]
    public void Processing_is_shown_as_soon_as_the_engine_reports_busy_even_without_the_key_event()
    {
        _listening.SetProcessing(true);
        Assert.Equal(AssistantPhase.Processing, Phase);
    }

    [Fact]
    public void Releasing_the_key_with_a_busy_engine_waits_for_the_result()
    {
        _listening.PushToTalkDown();
        _listening.SetProcessing(true);
        _listening.PushToTalkUp();
        Assert.Equal(AssistantPhase.Processing, Phase);
        Assert.Equal("", _svc.Current.HeardText);
    }

    [Fact]
    public void An_empty_utterance_becomes_no_match_not_a_hang()
    {
        _listening.PushToTalkDown();
        _listening.SetProcessing(true);
        _listening.PushToTalkUp();
        _listening.SetProcessing(false);
        _listening.Reject(UtteranceOutcome.Empty);
        Assert.Equal(AssistantPhase.NoMatch, Phase);
    }

    [Fact]
    public void A_transcription_failure_is_an_error_with_a_localizable_key()
    {
        _listening.SetProcessing(true);
        _listening.SetProcessing(false);
        _listening.Reject(UtteranceOutcome.Failed);
        Assert.Equal(AssistantPhase.Error, Phase);
        Assert.Equal("speech.transcribe-failed", _svc.Current.ErrorKey);
    }

    [Fact]
    public void A_text_that_matches_nothing_is_no_match()
    {
        _listening.SetProcessing(true);
        _pipeline.Raise(new PipelineEvent(PipelineStage.Heard, "كلام عشوائي"));
        _pipeline.Raise(new PipelineEvent(PipelineStage.NoCommand, "كلام عشوائي"));
        Assert.Equal(AssistantPhase.NoMatch, Phase);
        Assert.Equal("كلام عشوائي", _svc.Current.HeardText);
    }

    [Fact]
    public void A_failed_execution_is_an_error()
    {
        _pipeline.Raise(new PipelineEvent(PipelineStage.Heard, "افتح ديسكورد"));
        _pipeline.Raise(new PipelineEvent(PipelineStage.Matched, "افتح ديسكورد", "Open Discord"));
        _pipeline.Raise(new PipelineEvent(PipelineStage.Executed, "افتح ديسكورد", "Open Discord", Report: Report(false)));
        Assert.Equal(AssistantPhase.Error, Phase);
    }

    [Fact]
    public void The_microphone_failing_is_an_error_carrying_the_reason()
    {
        _listening.ErrorKey = "mic.disconnected";
        _listening.SetState(ListeningState.Error);
        Assert.Equal(AssistantPhase.Error, Phase);
        Assert.Equal("mic.disconnected", _svc.Current.ErrorKey);
    }

    [Fact]
    public void A_late_busy_notification_does_not_overwrite_a_result_on_screen()
    {
        _pipeline.Raise(new PipelineEvent(PipelineStage.Heard, "ارفع الصوت"));
        _pipeline.Raise(new PipelineEvent(PipelineStage.Matched, "ارفع الصوت", "Volume up"));
        _pipeline.Raise(new PipelineEvent(PipelineStage.Executed, "ارفع الصوت", "Volume up", Report: Report(true)));
        _listening.SetProcessing(true);
        Assert.Equal(AssistantPhase.Success, Phase);
    }
}
