# ScreenWatcher

Small Windows app (C# WinForms, .NET 10 LTS, dark Thai UI, no tray icon; X exits) that watches the
"ACE Online Kaze" game window, raises events (death, game dropped) and sends them to the user's own
Discord bot (Discord.Net, runs inside the app).

## Rules
- Reply in English.
- The app must stay strictly **read-only** toward the game: OS window enumeration and window/screen
  capture only. No input simulation, no DLL injection, no memory reading, no hooks into the game
  process. The game runs an anti-cheat ("KAZE Guard"). If capture is blocked, stop and report;
  never work around it.
- Keep CPU and RAM minimal (1 poll/second, reuse buffers, no per-tick allocations of frames).
- Test only what a task touches. No full build matrix, no publish.
- Never push, release, or delete anything without the owner's explicit word.

## Layout
- `src/ScreenWatcher/detectors.json`: every detector threshold and region (single source of truth;
  the tests load the same file).
- `src/ScreenWatcher/Capture`: window finder + Windows.Graphics.Capture (WGC) window capture, client area cropped from the
  captured frame. PrintWindow is not used (it made the game flash white).
- `src/ScreenWatcher/Detection`: `IFrameDetector` (add new frame detectors here, e.g. ammo), death
  analyzer + detector, game-drop detector.
- `src/ScreenWatcher/Collection`: optional "Collect data" mode (HUD crops + full frames saved to
  %LOCALAPPDATA%\ScreenWatcher\collect, size-capped; writes on a background thread).
- `src/ScreenWatcher/Alerts`: event -> Thai text + HUD crop PNG, queued and sent with retries.
- `src/ScreenWatcher/Bot`: Discord bot (Guilds intent only, slash command /ss). Token DPAPI-encrypted
  (`Config/TokenStore.cs`); never log or commit it (`Logging/AppLog` scrubs it).
- `src/ScreenWatcher/UI`: main window + settings page, built from `design/mockup-*.html`.
- `samples/`: reference images and their classification (`samples/README.md`).

## Commands
- Tests: `dotnet test tests/ScreenWatcher.Tests`
- Run: `dotnet run --project src/ScreenWatcher -c Release`
