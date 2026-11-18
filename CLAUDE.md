# ScreenWatcher

Small Windows tray app (C# WinForms, .NET 10 LTS) that watches the "ACE Online Kaze" game window
and raises events (death, game dropped).

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
- `samples/`: reference images and their classification (`samples/README.md`).

## Commands
- Tests: `dotnet test tests/ScreenWatcher.Tests`
- Run: `dotnet run --project src/ScreenWatcher -c Release`
