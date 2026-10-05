# Voice Commander v1.0.0

First public release. Offline voice control for Windows 10/11 (x64).

## Highlights

- **Voice control that stays on your PC.** Speech is recognized locally; no audio is uploaded, and there is no account, API key or telemetry.
- **English and Arabic.** English uses Vosk; Arabic uses a local Whisper model (Vosk Arabic models remain available as an alternative).
- **Custom commands and workflows.** Build multi-step commands in a visual editor (open/close/focus apps, folders, URLs, volume, screenshots, waits, keyboard shortcuts, notifications, lock) and test them with one click.
- **Floating assistant.** An always-on-top overlay showing listening, processing, the recognized phrase and the result, with a collapsed mode.
- **Push-to-talk or always listening.** `Ctrl+Space` by default, optional wake phrase, microphone selection with a live level meter, global hotkeys with conflict detection.
- **English and Arabic UI** with right-to-left layout and live language switching, in a true-black theme.

## Voice control

- Exact command matching with normalization (case, punctuation, Arabic letters, spoken numbers) and templates such as `volume {number}` and `open {app}`.
- Built-in commands for apps, folders, Settings pages, lock, screenshots, master and per-app volume, and media keys. Command packs: Windows, Media, Gaming, Discord.
- Power commands (shutdown, restart, sign out, sleep) are disabled by default and require confirmation.
- 1.5 s cooldown so one utterance does not trigger twice.
- Application registry with auto-detect, icons, test launch and "Locate…".
- `.voicecommander` import/export, local history, system tray, rotating logs.

## Speech engines and models

- English: Vosk (`vosk-model-small-en-us-0.15`).
- Arabic: Whisper via whisper.cpp (tiny, base, small, medium; small is the default). Downloads are verified against a published size and SHA-256 and resume after interruption.
- Models are **not** included in the installer. They are downloaded from inside the app, only when you press Download.

## Downloads

| File | Notes |
| --- | --- |
| `VoiceCommander-1.0.0-Setup.exe` | Per-user installer (into `%LOCALAPPDATA%\Programs\Voice Commander\`), no administrator rights, optional desktop shortcut. Uninstalling keeps your settings and models. |

Self-contained; .NET does not need to be installed.

## Known limitations

- The installer and executables are not code-signed, so Windows SmartScreen may show a warning.
- Accuracy depends on the chosen model; the small English model is fast but not perfect.
- Whisper transcribes after you stop speaking, so Arabic recognition has a short delay that grows with model size.
- Per-app volume only works for apps with an active audio session.
- Windows x64 only. Matching is exact by design; free-form natural language is not understood.
