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
- Never release or delete anything without the owner's explicit word. Pushing to origin main
  follows the GitHub upload rule below.

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
  row's "รีเซ็ต" button or Discord /reset). EXP card (`ExpCardDetector.cs`): blue emblem searched
  along the whole top buff bar (template in detectors.json `expCard`), "บัตร exp ครบ 30 นาที" after
  10 s absent; second line of the kill row with its own checkbox; not in the unreadable rule.
  Occlusion rule (all HUD rows): "empty/low" only on a positive read (digits read confidently, an
  item slot that positively looks empty, 00000 in the red digits); anything drawn over a part
  (tooltip, window, cursor) is unreadable and keeps the last value. Kill counter: the panel exists
  only while an EXP card is in use; done = last value >= 295 then no panel for 10 s; row shows
  "ครบแล้ว" / "ไม่มี"; only tooltip text over it makes it unreadable.
  War pillar (`WarPillar.cs`, detectors.json `warPillar`): icon boxes (blue line + light outline)
  searched along the strip under the gauge; HH:MM read pixel-exactly as Tahoma (11 px at 1.0);
  pillars tracked by remaining time (never slot); one alert per pillar at <= N minutes (row
  "เสาวอ แจ้งเมื่อเหลือ N นาที", 1..120, default 10); no panel = "ไม่มี", "?" only under tooltip text.
  Pillar HP: red bar under the timer inside the icon box (fill columns +8..+42, rows +43..+47 from
  the blue corner at 1.0; length from the track's dark top border); fill red/orange/yellow, else empty;
  HP never rises, a drop counts after 2 consecutive polls; one alert per pillar per level 75/50/25
  ("เสาวอเลือดเหลือ X%!", same checkbox and crop); row "00:15 (80%) · 00:42 (100%)", "+N" when long.
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
- `src/ScreenWatcher/Update`: notify-and-click updater (one-file exe only). Checks GitHub
  `releases/latest` on start and every 6 h (no token, silent on errors); on the banner click downloads
  `ScreenWatcher.exe` + `ScreenWatcher.exe.sig` to %TEMP%, verifies ECDSA P-256 with the public key
  in `UpdateSignature.PublicKey`, renames the running exe to `.old`, moves the new one in, restarts
  (`--after-update <pid>`), deletes `.old` on the next start.
- `tools/UpdateSigner`: release signer used by publish.ps1. Private key ONLY in
  `C:\ScreenWatcher-keys\update-signing-private.pem` (outside the repo; never commit, log or print it).
- `samples/`: reference images and their classification (`samples/README.md`).

## Commands
- Tests: `dotnet test tests/ScreenWatcher.Tests`
- Run: `dotnet run --project src/ScreenWatcher -c Release`
- Check the readers live without Discord: add `-- --no-send` (never connects or sends; alerts and
  row readings go to `%LOCALAPPDATA%\ScreenWatcher\log.txt`).
- One-file build for friends: `powershell -ExecutionPolicy Bypass -File .\publish.ps1` ->
  `dist\ScreenWatcher.exe` + `dist\ScreenWatcher.exe.sig` (self-contained win-x64 single file, no
  trimming, not compressed; dist\ is git-ignored and wiped first; signing needs the key above). Version: `<Version>` in `ScreenWatcher.csproj` (title shows
  Major.Minor). detectors.json, glyphs.json, icons.json and the guide are embedded resources
  (`AppFiles.cs`; a file next to the exe wins). Try a fresh install without touching real data:
  set `SCREENWATCHER_DATA` to an empty folder (replaces %LOCALAPPDATA%\ScreenWatcher).

## GitHub upload rule
- After every finished, committed change whose touched tests pass: push to origin main
  automatically (`git push origin main`, code backup only), without asking.
- Before every push, list the files changed since origin/main and stop and report (do not push) if
  one is over 50 MB, matches the exclusions below, or contains a token, key or other secret.
- release = only if the owner typed "release": create GitHub release vX.Y.Z from the csproj version with the signed dist exe; requires a prior test by the owner. Attach BOTH `ScreenWatcher.exe` and `ScreenWatcher.exe.sig` (exact names; the in-app updater ignores a release without both).
- Never push tokens, signing keys, samples/, dist/, bin/, obj/. Never force-push, delete branches, or delete releases.
