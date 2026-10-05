using VoiceCommander.Core.Actions;
using VoiceCommander.Core.Models;

namespace VoiceCommander.Core.Services;

public static class PackIds
{
    public const string Windows = "windows";
    public const string Media = "media";
    public const string Gaming = "gaming";
    public const string Discord = "discord";
}

/// <summary>
/// Starter commands. They are ordinary <see cref="VoiceCommand"/> records (same system as custom commands): editable,
/// deletable, and referencing applications by Id or by the {app} template placeholder.
/// </summary>
public static class BuiltInCommands
{
    /// <summary>Bump when new built-ins are added; missing ones are then merged in (unless the user deleted them).</summary>
    public const int SeedVersion = 2;

    public const string AppPlaceholder = "{app}";
    public const string NumberPlaceholder = "{number}";

    public static IReadOnlyList<CommandPack> Packs { get; } = new List<CommandPack>
    {
        new() { Id = PackIds.Windows, NameKey = "pack.windows", DescriptionKey = "pack.windows.desc", Icon = "" },
        new() { Id = PackIds.Media, NameKey = "pack.media", DescriptionKey = "pack.media.desc", Icon = "" },
        new() { Id = PackIds.Gaming, NameKey = "pack.gaming", DescriptionKey = "pack.gaming.desc", Icon = "" },
        new() { Id = PackIds.Discord, NameKey = "pack.discord", DescriptionKey = "pack.discord.desc", Icon = "" },
    };

    private static ActionStep Step(string type, params (string Key, string Value)[] p)
    {
        var s = new ActionStep { Type = type };
        foreach (var (k, v) in p) s.Parameters[k] = v;
        return s;
    }

    private static VoiceCommand Cmd(string id, string pack, CommandCategory cat, string icon, string[] phrases, ActionStep[] steps, bool enabled = true) => new()
    {
        Id = id, NameKey = "cmd." + id, DescriptionKey = "cmd." + id + ".desc", Name = id, PackId = pack, Category = cat, Icon = icon,
        IsBuiltIn = true, Enabled = enabled, Phrases = phrases.ToList(), Actions = steps.ToList(),
    };

    public static List<VoiceCommand> Create()
    {
        var list = new List<VoiceCommand>();
        const string W = PackIds.Windows, M = PackIds.Media;
        var app = ("app", AppPlaceholder);

        // ---- Applications (template based: works for every registered app) ----
        list.Add(Cmd("apps.open", W, CommandCategory.Applications, "",
            new[] { "open {app}", "launch {app}", "start {app}", "افتح {app}", "شغل {app}", "افتحي {app}" },
            new[] { Step(ActionTypes.OpenApp, app) }));
        list.Add(Cmd("apps.close", W, CommandCategory.Applications, "",
            new[] { "close {app}", "exit {app}", "quit {app}", "اغلق {app}", "سكر {app}", "اقفل {app}" },
            new[] { Step(ActionTypes.CloseApp, app) }));
        list.Add(Cmd("apps.restart", W, CommandCategory.Applications, "",
            new[] { "restart {app}", "reopen {app}", "اعد تشغيل {app}", "اعادة تشغيل {app}" },
            new[] { Step(ActionTypes.RestartApp, app) }));
        list.Add(Cmd("apps.focus", W, CommandCategory.Applications, "",
            new[] { "switch to {app}", "focus {app}", "go to {app}", "انتقل الى {app}", "حول الى {app}" },
            new[] { Step(ActionTypes.FocusApp, app) }));
        list.Add(Cmd("apps.minimize", W, CommandCategory.Applications, "",
            new[] { "minimize {app}", "صغر {app}" },
            new[] { Step(ActionTypes.MinimizeApp, app) }));
        list.Add(Cmd("apps.maximize", W, CommandCategory.Applications, "",
            new[] { "maximize {app}", "كبر {app}", "تكبير {app}" },
            new[] { Step(ActionTypes.MaximizeApp, app) }));

        // ---- Folders ----
        Folder(list, "downloads", "shell:Downloads", "", "open downloads", "open downloads folder", "افتح التنزيلات", "افتح مجلد التنزيلات");
        Folder(list, "documents", "shell:Personal", "", "open documents", "open documents folder", "افتح المستندات", "افتح مجلد المستندات");
        Folder(list, "desktop", "shell:Desktop", "", "open desktop folder", "open desktop", "افتح مجلد سطح المكتب");
        Folder(list, "pictures", "shell:My Pictures", "", "open pictures", "open pictures folder", "افتح الصور", "افتح مجلد الصور");
        Folder(list, "music", "shell:My Music", "", "open music folder", "افتح مجلد الموسيقى");
        Folder(list, "videos", "shell:My Video", "", "open videos folder", "افتح مجلد الفيديو");

        // ---- Settings ----
        Settings(list, "home", "ms-settings:", "open settings", "open windows settings", "افتح الاعدادات");
        Settings(list, "display", "ms-settings:display", "open display settings", "افتح اعدادات الشاشة");
        Settings(list, "sound", "ms-settings:sound", "open sound settings", "افتح اعدادات الصوت");
        Settings(list, "bluetooth", "ms-settings:bluetooth", "open bluetooth settings", "افتح اعدادات البلوتوث");
        Settings(list, "wifi", "ms-settings:network-wifi", "open wifi settings", "open network settings", "افتح اعدادات الواي فاي", "افتح اعدادات الشبكة");

        // ---- Windows ----
        list.Add(Cmd("windows.lock", W, CommandCategory.System, "",
            new[] { "lock computer", "lock screen", "lock pc", "lock windows", "اقفل الكمبيوتر", "قفل الشاشة" },
            new[] { Step(ActionTypes.Lock) }));
        list.Add(Cmd("windows.showdesktop", W, CommandCategory.System, "",
            new[] { "show desktop", "go to desktop", "اظهر سطح المكتب", "اعرض سطح المكتب" },
            new[] { Step(ActionTypes.ShowDesktop) }));
        list.Add(Cmd("windows.screenshot", W, CommandCategory.System, "",
            new[] { "take screenshot", "take a screenshot", "screenshot", "capture screen", "خذ لقطة شاشة", "لقطة شاشة", "صور الشاشة", "اعمل سكرين شوت", "سكرين شوت" },
            new[] { Step(ActionTypes.Screenshot, ("target", "screen")) }));
        list.Add(Cmd("windows.screenshot.window", W, CommandCategory.System, "",
            new[] { "screenshot window", "take window screenshot", "capture window", "لقطة نافذة", "صور النافذة" },
            new[] { Step(ActionTypes.Screenshot, ("target", "window")) }));

        // Dangerous: disabled by default; also gated by safety settings + confirmation dialog at run time.
        list.Add(Cmd("power.shutdown", W, CommandCategory.System, "",
            new[] { "shut down computer", "shutdown computer", "turn off computer", "اطفئ الكمبيوتر", "اغلاق الكمبيوتر" },
            new[] { Step(ActionTypes.Shutdown) }, enabled: false));
        list.Add(Cmd("power.restart", W, CommandCategory.System, "",
            new[] { "restart computer", "reboot computer", "اعد تشغيل الكمبيوتر" },
            new[] { Step(ActionTypes.Restart) }, enabled: false));
        list.Add(Cmd("power.signout", W, CommandCategory.System, "",
            new[] { "sign out", "log out", "تسجيل الخروج", "سجل خروج" },
            new[] { Step(ActionTypes.SignOut) }, enabled: false));
        list.Add(Cmd("power.sleep", W, CommandCategory.System, "",
            new[] { "sleep computer", "put computer to sleep", "نيم الكمبيوتر", "وضع السكون" },
            new[] { Step(ActionTypes.Sleep) }, enabled: false));

        // ---- Media / audio ----
        list.Add(Cmd("audio.up", M, CommandCategory.Audio, "",
            new[] { "volume up", "increase volume", "louder", "raise volume", "ارفع الصوت", "زد الصوت", "زيد الصوت", "علي الصوت", "صوت اعلى" },
            new[] { Step(ActionTypes.VolumeChange, ("amount", "10")) }));
        list.Add(Cmd("audio.down", M, CommandCategory.Audio, "",
            new[] { "volume down", "decrease volume", "lower volume", "quieter", "اخفض الصوت", "خفض الصوت", "قلل الصوت", "وطي الصوت", "نزل الصوت", "صوت اقل" },
            new[] { Step(ActionTypes.VolumeChange, ("amount", "-10")) }));
        list.Add(Cmd("audio.set", M, CommandCategory.Audio, "",
            new[] { "volume {number}", "set volume to {number}", "volume to {number}", "الصوت {number}", "اضبط الصوت على {number}", "مستوى الصوت {number}" },
            new[] { Step(ActionTypes.VolumeSet, ("value", NumberPlaceholder)) }));
        list.Add(Cmd("audio.max", M, CommandCategory.Audio, "",
            new[] { "max volume", "full volume", "اعلى صوت", "الصوت كامل" },
            new[] { Step(ActionTypes.VolumeSet, ("value", "100")) }));
        list.Add(Cmd("audio.mute", M, CommandCategory.Audio, "",
            new[] { "mute", "mute volume", "mute sound", "كتم الصوت", "اكتم الصوت", "سكر الصوت", "كتم" },
            new[] { Step(ActionTypes.Mute, ("mode", "mute")) }));
        list.Add(Cmd("audio.unmute", M, CommandCategory.Audio, "",
            new[] { "unmute", "unmute volume", "unmute sound", "الغاء كتم الصوت", "الغي كتم الصوت", "فك الكتم" },
            new[] { Step(ActionTypes.Mute, ("mode", "unmute")) }));
        list.Add(Cmd("audio.togglemute", M, CommandCategory.Audio, "",
            new[] { "toggle mute", "بدل الكتم" },
            new[] { Step(ActionTypes.Mute, ("mode", "toggle")) }));
        list.Add(Cmd("audio.app.set", M, CommandCategory.Audio, "",
            new[] { "{app} volume {number}", "set {app} volume to {number}", "صوت {app} {number}" },
            new[] { Step(ActionTypes.AppVolumeSet, app, ("value", NumberPlaceholder)) }));
        list.Add(Cmd("audio.app.mute", M, CommandCategory.Audio, "",
            new[] { "mute {app}", "اكتم {app}", "كتم {app}" },
            new[] { Step(ActionTypes.AppMute, app, ("mode", "mute")) }));
        list.Add(Cmd("audio.app.unmute", M, CommandCategory.Audio, "",
            new[] { "unmute {app}", "الغاء كتم {app}", "فك كتم {app}" },
            new[] { Step(ActionTypes.AppMute, app, ("mode", "unmute")) }));
        list.Add(Cmd("media.playpause", M, CommandCategory.Audio, "",
            new[] { "play", "pause", "play pause", "resume music", "pause music", "شغل الموسيقى", "ايقاف مؤقت", "توقف" },
            new[] { Step(ActionTypes.Hotkey, ("keys", "MediaPlayPause")) }));
        list.Add(Cmd("media.next", M, CommandCategory.Audio, "",
            new[] { "next track", "next song", "skip song", "الاغنية التالية", "التالي" },
            new[] { Step(ActionTypes.Hotkey, ("keys", "MediaNext")) }));
        list.Add(Cmd("media.previous", M, CommandCategory.Audio, "",
            new[] { "previous track", "previous song", "الاغنية السابقة", "السابق" },
            new[] { Step(ActionTypes.Hotkey, ("keys", "MediaPrevious")) }));

        // ---- Gaming: a ready-made multi-action workflow ----
        list.Add(Cmd("gaming.start", PackIds.Gaming, CommandCategory.Custom, "",
            new[] { "start gaming", "game mode", "let's play", "ابدا اللعب", "وضع الالعاب" },
            new[]
            {
                Step(ActionTypes.OpenApp, ("app", "app.discord")),
                Step(ActionTypes.Wait, ("seconds", "2")),
                Step(ActionTypes.OpenApp, ("app", "app.steam")),
                Step(ActionTypes.Wait, ("seconds", "1")),
                Step(ActionTypes.Notification, ("title", "Voice Commander"), ("message", "Gaming mode started")),
            }));
        list.Add(Cmd("gaming.stop", PackIds.Gaming, CommandCategory.Custom, "",
            new[] { "stop gaming", "end game mode", "انهي اللعب", "اوقف وضع الالعاب" },
            new[]
            {
                Step(ActionTypes.CloseApp, ("app", "app.steam")),
                Step(ActionTypes.CloseApp, ("app", "app.discord")),
            }));

        // ---- Discord ----
        list.Add(Cmd("discord.open", PackIds.Discord, CommandCategory.Applications, "",
            new[] { "open discord", "launch discord", "start discord", "افتح ديسكورد", "شغل ديسكورد" },
            new[] { Step(ActionTypes.OpenApp, ("app", "app.discord")) }));
        list.Add(Cmd("discord.close", PackIds.Discord, CommandCategory.Applications, "",
            new[] { "close discord", "exit discord", "quit discord", "سكر ديسكورد", "اغلق ديسكورد", "اقفل ديسكورد" },
            new[] { Step(ActionTypes.CloseApp, ("app", "app.discord")) }));
        list.Add(Cmd("discord.mute", PackIds.Discord, CommandCategory.Audio, "",
            new[] { "mute discord", "اكتم ديسكورد" },
            new[] { Step(ActionTypes.AppMute, ("app", "app.discord"), ("mode", "mute")) }));
        list.Add(Cmd("discord.unmute", PackIds.Discord, CommandCategory.Audio, "",
            new[] { "unmute discord", "فك كتم ديسكورد" },
            new[] { Step(ActionTypes.AppMute, ("app", "app.discord"), ("mode", "unmute")) }));
        list.Add(Cmd("discord.volume", PackIds.Discord, CommandCategory.Audio, "",
            new[] { "discord volume {number}", "صوت ديسكورد {number}" },
            new[] { Step(ActionTypes.AppVolumeSet, ("app", "app.discord"), ("value", NumberPlaceholder)) }));

        return list;
    }

    private static void Folder(List<VoiceCommand> list, string key, string path, string icon, params string[] phrases) =>
        list.Add(Cmd("folder." + key, PackIds.Windows, CommandCategory.System, icon, phrases,
            new[] { Step(ActionTypes.OpenFolder, ("path", path)) }));

    private static void Settings(List<VoiceCommand> list, string key, string uri, params string[] phrases) =>
        list.Add(Cmd("settings." + key, PackIds.Windows, CommandCategory.System, "", phrases,
            new[] { Step(ActionTypes.OpenSettings, ("uri", uri)) }));
}
