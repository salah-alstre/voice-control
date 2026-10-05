using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using VoiceCommander.App.Dialogs;
using VoiceCommander.App.ViewModels;
using VoiceCommander.App.Views;
using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Localization;
using VoiceCommander.Core.Models;
using VoiceCommander.Core.Pipeline;
using VoiceCommander.Core.Services;
using VoiceCommander.Core.Speech;
using VoiceCommander.Core.Windows;

namespace VoiceCommander.App.Infrastructure;

/// <summary>
/// Developer-only (<c>--render-shots &lt;dir&gt;</c>): renders every page, dialog and assistant state to PNG files off screen,
/// against an isolated temporary data folder, so the UI can be inspected without driving the desktop.
/// </summary>
internal static class RenderHarness
{
    /// <summary>Stands in for the real state service so each assistant phase can be shown on demand.</summary>
    internal sealed class FakeState : IAssistantStateService
    {
        public AssistantSnapshot Current { get; private set; } = AssistantSnapshot.Idle;
        public ListeningState ListeningState { get; set; } = ListeningState.Stopped;
        public event Action<AssistantSnapshot>? Changed;
#pragma warning disable CS0067
        public event Action? ListeningStarted;
        public event Action? ListeningStopped;
        public event Action<string>? SpeechRecognized;
        public event Action<string>? CommandMatched;
        public event Action<string>? CommandExecutionStarted;
        public event Action<ExecutionReport>? CommandExecutionSucceeded;
        public event Action<string>? CommandExecutionFailed;
#pragma warning restore CS0067
        public void Set(AssistantSnapshot s) { Current = s; Changed?.Invoke(s); }
        public void Start() { }
        public void Dispose() { }
    }

    internal static readonly FakeState Fake = new();

    /// <summary>Release screenshots only: a listening service that is simply "ready" (no engines, no microphone).</summary>
    internal sealed class FakeListening : IListeningService
    {
        public ListeningState State { get; set; } = ListeningState.Idle;
        public string? ErrorKey => null;
        public bool IsEnabled => true;
        public bool IsProcessing => false;
#pragma warning disable CS0067
        public event Action? StateChanged;
        public event Action<float>? LevelChanged;
        public event Action? ProcessingChanged;
        public event Action<UtteranceOutcome>? UtteranceRejected;
#pragma warning restore CS0067
        public Task StartAsync() => Task.CompletedTask;
        public void SetEnabled(bool enabled) { }
        public void Toggle() { }
        public void PushToTalkDown() { }
        public void PushToTalkUp() { }
        public Task ApplySettingsAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private static string _dir = "";
    private static Dispatcher D => Application.Current.Dispatcher;

    public static async Task RunAsync(IServiceProvider sp, string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        var settings = sp.GetRequiredService<ISettingsService>();
        var loc = sp.GetRequiredService<ILocalizer>();
        var apps = sp.GetRequiredService<IAppRegistry>();
        var commands = sp.GetRequiredService<ICommandRepository>();
        var history = sp.GetRequiredService<IHistoryService>();

        // ---- seed (isolated temp data folder) ----
        var cs2 = new AppDefinition { Name = "Counter-Strike 2", ExecutablePath = "steam://rungameid/730", ProcessName = "cs2.exe" };
        var notepad = new AppDefinition { Name = "Notepad", ExecutablePath = @"C:\Windows\System32\notepad.exe", ProcessName = "notepad.exe" };
        var ghost = new AppDefinition { Name = "Old Tool", ExecutablePath = @"C:\Nope\gone\oldtool.exe", ProcessName = "oldtool.exe" };
        apps.AddOrUpdate(cs2); apps.AddOrUpdate(notepad); apps.AddOrUpdate(ghost);

        var openCs2 = new VoiceCommand
        {
            Name = "Open CS2", Category = CommandCategory.Applications, Icon = "\uE768",
            Phrases = new() { "Open CS2", "Start CS2", "Launch CS2", "افتح سي اس", "شغل سي اس" },
            Actions = new() { new ActionStep(ActionTypes.OpenApp, ("app", cs2.Id)) },
        };
        var openOld = new VoiceCommand
        {
            Name = "Open Old Tool", Category = CommandCategory.Custom, Icon = "\uE768",
            Phrases = new() { "Open old tool" },
            Actions = new() { new ActionStep(ActionTypes.OpenApp, ("app", ghost.Id)) },
        };
        var openMissing = new VoiceCommand
        {
            Name = "Open Deleted App", Category = CommandCategory.Custom, Icon = "\uE768",
            Phrases = new() { "Open deleted app" },
            Actions = new() { new ActionStep(ActionTypes.OpenApp, ("app", "deadbeefdeadbeef")) },
        };
        var gameMode = new VoiceCommand
        {
            Name = "Game Mode", Category = CommandCategory.Custom, Icon = "\uE768",
            Phrases = new() { "Game mode", "Start gaming" },
            Actions = new() { new ActionStep(ActionTypes.OpenApp, ("app", cs2.Id)), new ActionStep(ActionTypes.OpenApp, ("app", notepad.Id)) },
        };
        foreach (var c in new[] { openCs2, openOld, openMissing, gameMode }) commands.AddOrUpdate(c);

        var now = DateTime.Now;
        history.Add(new HistoryEntry { Timestamp = now.AddMinutes(-2), RecognizedText = "open cs2", CommandId = openCs2.Id, CommandName = "Open CS2", Outcome = ExecutionOutcome.Success, Message = "Counter-Strike 2 opened", DurationMs = 180, Source = TriggerSource.Voice, Confidence = 0.93 });
        history.Add(new HistoryEntry { Timestamp = now.AddMinutes(-9), RecognizedText = "open disk", Outcome = ExecutionOutcome.NoMatch, Message = "No matching command", DurationMs = 12, Source = TriggerSource.Voice, Confidence = 0.41 });
        history.Add(new HistoryEntry { Timestamp = now.AddHours(-1), RecognizedText = "افتح سي اس", CommandId = openCs2.Id, CommandName = "Open CS2", Outcome = ExecutionOutcome.Success, Message = "ok", DurationMs = 210, Source = TriggerSource.Voice, Confidence = 0.88 });
        history.Add(new HistoryEntry { Timestamp = now.AddHours(-3), RecognizedText = "open old tool", CommandId = openOld.Id, CommandName = "Open Old Tool", Outcome = ExecutionOutcome.Failed, Message = "Application executable was not found.", DurationMs = 40, Source = TriggerSource.Voice, Confidence = 0.9 });

        var s = settings.Current;
        s.OnboardingCompleted = true; s.DeveloperMode = true; s.AssistantAlwaysOnTop = true;
        settings.Save();

        foreach (var lang in new[] { "en", "ar" })
        {
            loc.SetLanguage(lang);
            await Pump();
            await ShootMain(sp, lang);
            await ShootEditors(sp, lang, openCs2, openOld, openMissing, gameMode, cs2);
            await ShootAssistant(sp, lang, settings, cs2);
            await ShootMisc(sp, lang);
        }
    }

    /// <summary>
    /// Release screenshots (<c>--render-release-shots &lt;dir&gt;</c>): the same real UI, but with a tidy demo data set -
    /// no error, no-match or missing-app states and no developer mode - written as <c>{lang}_{name}.png</c>.
    /// </summary>
    public static async Task RunReleaseAsync(IServiceProvider sp, string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        var settings = sp.GetRequiredService<ISettingsService>();
        var loc = sp.GetRequiredService<ILocalizer>();
        var apps = sp.GetRequiredService<IAppRegistry>();
        var commands = sp.GetRequiredService<ICommandRepository>();
        var history = sp.GetRequiredService<IHistoryService>();
        var locator = sp.GetRequiredService<IAppLocator>();

        // Only keep starter apps that exist on this machine, so the Applications page has no "not found" rows.
        foreach (var a in apps.Apps.ToList())
            if (a.IsBuiltIn && locator.Resolve(a) == null) apps.Remove(a.Id);

        var cs2 = new AppDefinition { Name = "Counter-Strike 2", ExecutablePath = "steam://rungameid/730", ProcessName = "cs2.exe", Aliases = new() { "cs2", "counter strike" } };
        apps.AddOrUpdate(cs2);
        var present = apps.Apps.Select(a => a.Id).ToHashSet();

        // Built-in commands whose app is not installed here would show a warning; drop them for the demo data set.
        foreach (var c in commands.Commands.ToList())
        {
            var missing = c.Actions.Any(st => st.Parameters.TryGetValue("app", out var id) && !string.IsNullOrEmpty(id) && !id.StartsWith('{') && !present.Contains(id));
            if (missing) commands.Remove(c.Id);
        }

        // The starter "Gaming" pack ships its own start/stop commands; the demo uses a custom "Start Gaming" instead, so drop the
        // pack's start command to avoid a duplicate-phrase warning.
        commands.Remove("gaming.start");

        var gaming = new VoiceCommand
        {
            Name = "Start Gaming", Category = CommandCategory.Custom, Icon = "",
            Phrases = new() { "Start gaming", "Game mode", "ابدأ اللعب" },
            Actions = new()
            {
                new ActionStep(ActionTypes.OpenApp, ("app", "app.discord")),
                new ActionStep(ActionTypes.OpenApp, ("app", "app.steam")),
                new ActionStep(ActionTypes.Wait, ("seconds", "2")),
                new ActionStep(ActionTypes.OpenApp, ("app", cs2.Id)),
                new ActionStep(ActionTypes.VolumeSet, ("value", "40")),
            },
        };
        commands.AddOrUpdate(gaming);

        var now = DateTime.Now;
        void H(int minutesAgo, string heard, string name, string msg, int ms, double conf) =>
            history.Add(new HistoryEntry { Timestamp = now.AddMinutes(-minutesAgo), RecognizedText = heard, CommandId = name, CommandName = name, Outcome = ExecutionOutcome.Success, Message = msg, DurationMs = ms, Source = TriggerSource.Voice, Confidence = conf });
        H(1, "open notepad", "Open Notepad", "Notepad opened", 140, 0.96);
        H(4, "volume down", "Volume down", "Volume 35%", 60, 0.94);
        H(11, "افتح ديسكورد", "Open Discord", "Discord opened", 190, 0.91);
        H(26, "take screenshot", "Take screenshot", "Screenshot saved", 320, 0.95);
        H(58, "start gaming", "Start Gaming", "5 actions completed", 2400, 0.9);
        H(95, "وطي الصوت", "Volume down", "Volume 30%", 70, 0.89);

        var s = settings.Current;
        s.OnboardingCompleted = true; s.DeveloperMode = false; s.AssistantAlwaysOnTop = true; s.AssistantCollapsed = false;
        settings.Save();

        var notepad = apps.Apps.First(a => a.Id == "app.notepad");
        var icon = sp.GetRequiredService<IIconService>().GetIconPath(notepad);
        AssistantSnapshot Snap(AssistantPhase p, string heard, string? cmd, string? target, string? ic) => new(p, heard, cmd, target, ic, null, null, false);

        foreach (var lang in new[] { "en", "ar" })
        {
            loc.SetLanguage(lang);
            await Pump();

            var main = sp.GetRequiredService<MainViewModel>();
            var win = new MainWindow(main, settings) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            win.Show();
            main.Start();
            await Pump(900);
            Fake.ListeningState = ListeningState.Stopped;
            foreach (var (key, file) in new[] { ("dashboard", "dashboard"), ("commands", "commands"), ("workflows", "workflows"), ("applications", "applications"), ("microphone", "microphone"), ("settings", "settings") })
            {
                main.Navigate(key);
                await Pump(900);
                CaptureWindow(win, $"{lang}_{file}");
            }
            win.Close();
            await Pump();

            // command editor on the workflow
            var picker = new AppPickerContext(apps, sp.GetRequiredService<IIconService>(), sp.GetRequiredService<IAppEditorService>(), locator, sp.GetRequiredService<IDialogService>());
            var vm = new CommandEditorViewModel(gaming, commands, apps, sp.GetRequiredService<IActionHandlerRegistry>(),
                sp.GetRequiredService<ICommandExecutor>(), sp.GetRequiredService<IDialogService>(), loc, false, picker);
            var ed = new CommandEditorWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            ed.Show();
            await Pump(1000);
            CaptureWindow(ed, $"{lang}_command-editor");
            ed.Close();
            await Pump();

            // floating assistant
            settings.Current.AssistantCollapsed = false; // the previous language pass collapses it at the end
            var avm = new AssistantViewModel(Fake, settings, sp.GetRequiredService<IListeningService>(), sp.GetRequiredService<IRecoveryService>());
            var aw = new FloatingAssistantWindow(avm, settings);
            Fake.Set(AssistantSnapshot.Idle);
            aw.Show();
            await Pump(700);
            Fake.ListeningState = ListeningState.Listening;
            Fake.Set(Snap(AssistantPhase.Listening, "", null, null, null));
            await Pump(700);
            Capture(aw, $"{lang}_assistant-listening");
            Fake.Set(Snap(AssistantPhase.Processing, "Open Notepad", null, null, null));
            await Pump(500);
            Capture(aw, $"{lang}_assistant-processing");
            Fake.ListeningState = ListeningState.Stopped;
            Fake.Set(Snap(AssistantPhase.Success, "Open Notepad", "Open Notepad", "Notepad", icon));
            await Pump(700);
            Capture(aw, $"{lang}_assistant-success");
            Fake.Set(AssistantSnapshot.Idle);
            await Pump(300);
            avm.CollapseCommand.Execute(null);
            await Pump(600);
            Capture(aw, $"{lang}_assistant-collapsed");
            aw.Close();
            avm.Dispose();
            await Pump();
        }
    }

    // ---------------- main window ----------------
    private static async Task ShootMain(IServiceProvider sp, string lang)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var main = sp.GetRequiredService<MainViewModel>();
        var win = new MainWindow(main, settings) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        win.Show();
        main.Start();
        await Pump(600);

        foreach (var key in new[] { "dashboard", "commands", "workflows", "applications", "history", "microphone", "settings", "developer" })
        {
            main.Navigate(key);
            await Pump(500);
            Capture(win, $"{lang}_main_{key}");
        }

        // dashboard in each assistant phase (same state source as the mini window)
        main.Navigate("dashboard");
        foreach (var (name, snap) in Phases(null, sp.GetRequiredService<ILocalizer>()))
        {
            Fake.ListeningState = snap.Phase == AssistantPhase.Listening ? ListeningState.Listening : ListeningState.Stopped;
            Fake.Set(snap);
            await Pump(450);
            Capture(win, $"{lang}_main_dashboard_{name}");
        }
        Fake.Set(AssistantSnapshot.Idle);
        win.Close();
        await Pump();
    }

    // ---------------- editors ----------------
    private static async Task ShootEditors(IServiceProvider sp, string lang, VoiceCommand cs2Cmd, VoiceCommand oldCmd, VoiceCommand missingCmd, VoiceCommand workflow, AppDefinition cs2)
    {
        var repo = sp.GetRequiredService<ICommandRepository>();
        var picker = new AppPickerContext(sp.GetRequiredService<IAppRegistry>(), sp.GetRequiredService<IIconService>(), sp.GetRequiredService<IAppEditorService>(),
            sp.GetRequiredService<IAppLocator>(), sp.GetRequiredService<IDialogService>());

        async Task Editor(string name, VoiceCommand? cmd, Action<CommandEditorViewModel, CommandEditorWindow>? tweak = null, bool wf = false)
        {
            var vm = new CommandEditorViewModel(cmd, repo, sp.GetRequiredService<IAppRegistry>(), sp.GetRequiredService<IActionHandlerRegistry>(),
                sp.GetRequiredService<ICommandExecutor>(), sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<ILocalizer>(), wf, picker);
            var w = new CommandEditorWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            await Pump(500);
            tweak?.Invoke(vm, w);
            await Pump(500);
            Capture(w, $"{lang}_editor_{name}");
            w.Close();
            await Pump(100);
        }

        await Editor("cs2", cs2Cmd);
        await Editor("workflow", workflow);
        await Editor("app_exe_missing", oldCmd);
        await Editor("app_deleted", missingCmd);
        await Editor("new", null);

        // dropdown open - popups live in their own HWND, so render the popup child separately
        {
            var vm = new CommandEditorViewModel(cs2Cmd, repo, sp.GetRequiredService<IAppRegistry>(), sp.GetRequiredService<IActionHandlerRegistry>(),
                sp.GetRequiredService<ICommandExecutor>(), sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<ILocalizer>(), false, picker);
            var w = new CommandEditorWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            await Pump(500);
            var sel = vm.Steps.FirstOrDefault()?.AppSelector;
            if (sel != null) sel.IsOpen = true;
            await Pump(600);
            Capture(w, $"{lang}_editor_cs2_dropdown_window");
            foreach (var p in Find<Popup>(w).Where(p => p.IsOpen && p.Child is FrameworkElement))
            {
                // Render from the popup's own root so any RTL mirroring the popup host applies is included.
                var root = PresentationSource.FromVisual(p.Child)?.RootVisual as FrameworkElement ?? (FrameworkElement)p.Child;
                Capture(root, $"{lang}_editor_cs2_dropdown_popup");
            }
            w.Close();
        }

        // app editor
        var appEd = sp.GetRequiredService<IAppRegistry>();
        foreach (var (name, app) in new[] { ("cs2", cs2), ("new", (AppDefinition?)null) })
        {
            var vm = new AppEditorViewModel(app, null, appEd, sp.GetRequiredService<IAppController>(), sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<IIconService>());
            var w = new AppEditorWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            await Pump(700);
            Capture(w, $"{lang}_appeditor_{name}");
            w.Close();
        }
        {
            var vm = new AppEditorViewModel(null, @"C:\Windows\System32\notepad.exe", appEd, sp.GetRequiredService<IAppController>(), sp.GetRequiredService<IDialogService>(), sp.GetRequiredService<IIconService>());
            var w = new AppEditorWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            await Pump(900);
            Capture(w, $"{lang}_appeditor_browse_autodetect");
            w.Close();
        }
    }

    // ---------------- floating assistant ----------------
    private static IEnumerable<(string, AssistantSnapshot)> Phases(string? iconPath, ILocalizer loc) => new (string, AssistantSnapshot)[]
    {
        ("ready", AssistantSnapshot.Idle),
        ("listening", new(AssistantPhase.Listening, "", null, null, null, null, null, false)),
        ("processing", new(AssistantPhase.Processing, "Open CS2", null, null, null, null, null, false)),
        ("executing", new(AssistantPhase.Executing, "Open CS2", "Open CS2", "Counter-Strike 2", iconPath, null, null, false)),
        ("success", new(AssistantPhase.Success, "Open CS2", "Open CS2", "Counter-Strike 2", iconPath, null, null, false)),
        ("nomatch", new(AssistantPhase.NoMatch, "Open Disk", null, null, null, null, null, false)),
        ("error", new(AssistantPhase.Error, "Open old tool", "Open Old Tool", "Old Tool", iconPath, loc["edit.app.exe.missing"], null, true)),
    };

    private static async Task ShootAssistant(IServiceProvider sp, string lang, ISettingsService settings, AppDefinition cs2)
    {
        settings.Current.AssistantCollapsed = false;
        var icon = sp.GetRequiredService<IIconService>().GetIconPath(new AppDefinition { Name = "Notepad", ExecutablePath = @"C:\Windows\System32\notepad.exe", ProcessName = "notepad.exe" });
        Fake.Set(AssistantSnapshot.Idle);
        var vm = new AssistantViewModel(Fake, settings, sp.GetRequiredService<IListeningService>(), sp.GetRequiredService<IRecoveryService>());
        var w = new FloatingAssistantWindow(vm, settings);
        w.Show();
        await Pump(600);
        var locz = sp.GetRequiredService<ILocalizer>();
        foreach (var (name, snap) in Phases(icon, locz))
        {
            Fake.Set(snap);
            await Pump(500);
            Capture(w, $"{lang}_assistant_{name}");
        }
        Fake.Set(AssistantSnapshot.Idle);
        await Pump(300);
        vm.CollapseCommand.Execute(null);
        await Pump(500);
        Capture(w, $"{lang}_assistant_pill_ready");
        Fake.Set(Phases(icon, locz).First(p => p.Item1 == "listening").Item2);
        await Pump(500);
        Capture(w, $"{lang}_assistant_pill_listening");
        vm.ExpandCommand.Execute(null);
        Fake.Set(AssistantSnapshot.Idle);
        await Pump(300);
        w.Close();
        vm.Dispose();
        await Pump(100);
    }

    // ---------------- confirm dialog / onboarding ----------------
    private static async Task ShootMisc(IServiceProvider sp, string lang)
    {
        var loc = sp.GetRequiredService<ILocalizer>();
        var c = new ConfirmDialog(loc["common.delete"], loc["common.delete"] + "?", loc["common.delete"], loc["common.cancel"], true)
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        c.Show();
        await Pump(400);
        Capture(c, $"{lang}_confirm");
        c.Close();

        var ovm = ActivatorUtilities.CreateInstance<OnboardingViewModel>(sp);
        var o = new OnboardingWindow(ovm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        o.Show();
        await Pump(700);
        Capture(o, $"{lang}_onboarding");
        o.Close();
        await Pump(100);
    }

    // ---------------- helpers ----------------
    private static async Task Pump(int ms = 300)
    {
        await Task.Delay(ms);
        await D.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(60);
        await D.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var ch = VisualTreeHelper.GetChild(root, i);
            if (ch is T t) yield return t;
            foreach (var d in Find<T>(ch)) yield return d;
        }
        // Popups sit in the logical tree
        if (root is FrameworkElement fe)
            foreach (var l in LogicalTreeHelper.GetChildren(fe).OfType<DependencyObject>().Where(x => x is Popup))
                if (l is T lt) yield return lt;
    }

    /// <summary>
    /// Renders the whole window (so the true-black window background is included and RTL mirroring stays correct), then crops
    /// to the client content: the non-client strip is not part of the visual tree and would come out blank.
    /// </summary>
    private static void CaptureWindow(Window w, string name) => Capture(w, name, w.Content as FrameworkElement);

    private static void Capture(FrameworkElement el, string name, FrameworkElement? crop = null)
    {
        try
        {
            el.UpdateLayout();
            var w = (int)Math.Ceiling(el.ActualWidth); var h = (int)Math.Ceiling(el.ActualHeight);
            if (w < 2 || h < 2) { File.WriteAllText(Path.Combine(_dir, name + ".txt"), $"empty size {w}x{h}"); return; }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(el);
            BitmapSource result = rtb;
            if (crop != null)
            {
                // TransformBounds (not Transform of the origin): under RTL the content's top-left maps to a mirrored point.
                var b = crop.TransformToAncestor(el).TransformBounds(new Rect(0, 0, crop.ActualWidth, crop.ActualHeight));
                var x = Math.Max(0, (int)Math.Round(b.X)); var y = Math.Max(0, (int)Math.Round(b.Y));
                var cw = Math.Min(w - x, (int)Math.Ceiling(b.Width)); var ch = Math.Min(h - y, (int)Math.Ceiling(b.Height));
                if (cw > 1 && ch > 1) result = new CroppedBitmap(rtb, new Int32Rect(x, y, cw, ch));
            }
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(result));
            using var fs = File.Create(Path.Combine(_dir, name + ".png"));
            enc.Save(fs);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(_dir, name + ".err.txt"), ex.ToString()); }
    }
}
