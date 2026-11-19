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
- `src/ScreenWatcher/Detection/Hud`: HUD value readers and their alert logic (`IHudDetector`): ammo
  counters, fuel arc, kill counter, item bar. Digit shapes for ammo/kill are in `glyphs.json`, item
  icons in `icons.json` (both built from the labelled crops in `samples/labelled`); item counts are
  Tahoma digits rendered at start-up (`CountFont.cs`). Readings measured so far and what is still
  unverified: `samples/README.md`. Also here: the level-up quest panel (`QuestPanel*.cs`, its own
  message instead of the generic unreadable one) and the system message box (`SystemMessage.cs`,
  `InventoryDetector.cs`: row "ช่องเก็บของเต็ม", phrase matched as Tahoma 12 px, latched until the
  row's "รีเซ็ต" button or Discord /reset).
- `src/ScreenWatcher/Collection`: optional "Collect data" mode (HUD crops + full frames saved to
  %LOCALAPPDATA%\ScreenWatcher\collect, size-capped; writes on a background thread).
- `src/ScreenWatcher/Diagnostics`: field-trial diagnostics, all under %LOCALAPPDATA%\ScreenWatcher:
  event journal (`journal\yyyy-MM-dd.jsonl`, 20 MB cap), alert evidence and /miss bundles
  (`evidence\`, 200 MB cap), hourly row statistics, in-memory HUD ring (~10 s). Collection modes
  ปิด / อัตโนมัติ / เปิดตลอด; the auto rules are `Collection/AutoCollectRules.cs`, numbers in
  detectors.json `diagnostics`.
- `src/ScreenWatcher/Alerts`: event -> Thai text + PNG (death: full client frame; HUD rows: crop of
  that HUD part), queued and sent with retries.
- `src/ScreenWatcher/Bot`: Discord bot (Guilds intent only, slash commands /ss, /miss, /reset, /exp). Token DPAPI-encrypted
  (`Config/TokenStore.cs`); never log or commit it (`Logging/AppLog` scrubs it).
- `src/ScreenWatcher/ExpTest`: "ทดสอบ EXP" (button + Discord /exp): exp gained over N minutes from the
  level line's percentage (`LevelReader.ReadPercent`, read only while a test runs), kills from the
  kill counter, map label crop (`Detection/Hud/MapLabel.cs`, compared on text pixels). Results in
  %LOCALAPPDATA%\ScreenWatcher\exp-tests.
- `src/ScreenWatcher/UI`: main window + settings page, built from `design/mockup-*.html`.
- `samples/`: reference images and their classification (`samples/README.md`).

## Commands
- Tests: `dotnet test tests/ScreenWatcher.Tests`
- Run: `dotnet run --project src/ScreenWatcher -c Release`
- Check the readers live without Discord: add `-- --no-send` (never connects or sends; alerts and
  row readings go to `%LOCALAPPDATA%\ScreenWatcher\log.txt`).
