using Microsoft.Extensions.DependencyInjection;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Actions.Handlers;
using VoiceCommander.Core.Infrastructure;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Matching;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;
using VoiceCommander.Core.Windows;
using VoiceCommander.App.ViewModels;

namespace VoiceCommander.App.Infrastructure;

/// <summary>Composition root. Core has no DI extension on purpose; everything is wired here.</summary>
public static class AppHost
{
    /// <param name="overrides">Optional hook used only by the offscreen render harness to swap services.</param>
    public static IServiceProvider Build(Action<IServiceCollection>? overrides = null)
    {
        var s = new ServiceCollection();

        s.AddSingleton<ILogService>(_ => new LogService());
        s.AddSingleton<ISettingsService, SettingsService>();
        s.AddSingleton<ILocalizer, Localizer>();
        s.AddSingleton<ICommandRepository, CommandRepository>();
        s.AddSingleton<IAppRegistry, AppRegistry>();
        s.AddSingleton<IHistoryService, HistoryService>();
        s.AddSingleton<IImportExportService, ImportExportService>();

        s.AddSingleton<IAppLocator, AppLocator>();
        s.AddSingleton<IAppDetector, AppDetector>();
        s.AddSingleton<IAppController, AppController>();
        s.AddSingleton<IIconService, IconService>();
        s.AddSingleton<IAudioService, AudioService>();
        s.AddSingleton<IShellService, ShellService>();
        s.AddSingleton<IScreenshotService, ScreenshotService>();
        s.AddSingleton<IStartupService, StartupService>();

        s.AddSingleton<DialogService>();
        s.AddSingleton<IDialogService>(p => p.GetRequiredService<DialogService>());
        s.AddSingleton<IConfirmationService>(p => p.GetRequiredService<DialogService>());
        s.AddSingleton<TrayService>();
        s.AddSingleton<INotifier>(p => p.GetRequiredService<TrayService>());

        s.AddSingleton<IEnumerable<ICommandActionHandler>>(p => ActionHandlerFactory.CreateAll(
            p.GetRequiredService<IAppRegistry>(), p.GetRequiredService<IAppController>(), p.GetRequiredService<IAudioService>(),
            p.GetRequiredService<IScreenshotService>(), p.GetRequiredService<IShellService>(), p.GetRequiredService<ISettingsService>(),
            p.GetRequiredService<INotifier>()).ToList());
        s.AddSingleton<IActionHandlerRegistry, ActionHandlerRegistry>();
        s.AddSingleton<CooldownGate>();
        s.AddSingleton<ICommandMatcher, CommandMatcher>();
        s.AddSingleton<ICommandExecutor, CommandExecutor>();
        s.AddSingleton<IRecognitionPipeline, RecognitionPipeline>();

        s.AddSingleton<ISpeechModelManager, SpeechModelManager>();
        s.AddSingleton<IWhisperModelManager, WhisperModelManager>();
        s.AddSingleton<ISpeechEngineManager, SpeechEngineManager>();
        s.AddSingleton<IMicrophoneService, MicrophoneService>();
        s.AddSingleton<IHotkeyService, HotkeyService>();
        s.AddSingleton<IListeningService, ListeningService>();
        s.AddSingleton<IPttSoundService, PttSoundService>();
        s.AddSingleton<IAssistantStateService, AssistantStateService>();

        s.AddSingleton<MainViewModel>();
        s.AddSingleton<IRecoveryService, RecoveryService>();
        s.AddSingleton<SettingsApplier>();
        s.AddSingleton<DashboardViewModel>();
        s.AddSingleton<ICommandEditorService, CommandEditorService>();
        s.AddSingleton<CommandsViewModel>();
        s.AddSingleton<WorkflowsViewModel>();
        s.AddSingleton<IAppEditorService, AppEditorService>();
        s.AddSingleton<IOnboardingService, OnboardingService>();
        s.AddSingleton<ApplicationsViewModel>();
        s.AddSingleton<HistoryViewModel>();
        s.AddSingleton<MicrophoneViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<DeveloperViewModel>();
        s.AddSingleton<IAssistantController, AssistantController>();

        overrides?.Invoke(s);
        return s.BuildServiceProvider();
    }
}
