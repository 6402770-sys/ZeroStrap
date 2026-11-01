# ZeroStrap — development handoff

Read this first in a new session. Keep it short and factual; update it at each milestone.

- **Plan (scope + decisions):** ZeroStrap Expansion Plan v2 — https://claude.ai/artifact/YSqPU62S3HnVyMfXFLAmeU (sections 17–22 hold security rules, tests, roadmap, acceptance, files per phase).
- **Branch:** `zerostrap-expansion` (base `main`). Nothing pushed or released; both need explicit approval.
- **Untracked, not ours:** `PR_DESCRIPTION.md` — leave it.

## Status (2026-11-01)

| Phase | Commit | State |
|---|---|---|
| Baseline (CI paths, tests project, PathValidator) | `f63bd99` | done |
| 0 Navigation (named Configure tabs, studio sidebar entries) | `ef06dc1` | done |
| 1 Update false-prompt fix | `ba25822` | done |
| 2 FastFlags page + editor UI rework | `1366252` | done |
| 3 Zero FastFlags collections | `4d7a144` | done |
| 4 About redesign | `201f94e` | done |
| 5a/5b Theme engine + Theme Studio UI | `960a372`, `634dc26` | done |
| 6a/6b Tokenisation; image/video backgrounds | `de0abaf`, `c9c9cf7` | done |
| 7a Mods Studio engine | `bea7c26` | done |
| 7b Mods Studio UI (Installed/Library/Conflicts, badge, counts) | `b2ad623` | done |
| 8a Mods import engine (`ModImporter`, extractor link/allowlist/limits, layouts) | `3791ff7` | done — 606/606 tests |
| 8b Mods import UI (drop zone, preview, install) | see `git log` | done — 617/617 tests, preview smoke-tested |
| 9 FPS Unlocker | — | deferred (do not build) |
| 10a Macro Studio engine (recorder, player, hotkeys, runtime, store) | `1c231d4` | done — adversarial review (17 findings) fixed |
| 10a Macro Studio list editor | `9cd8acf` | done — 1034/1034 tests, UI smoke-tested |
| 10b Macro Studio timeline, camera inspector, import/export polish | `8e8adf3` | done — 1072/1072 tests, timeline smoke-tested |
| 11a Macro engine: game-key shortcuts, 8 concurrent macros, shared key ownership | `11fbdc8` | done — 1147/1147 tests |
| 11b Macro Studio UI: Stop all, running dots, shared-shortcut note, in-game overlay | `3f0597c` | done — 1151/1151 tests, UI smoke-tested |

## Verification
- 2026-11-01 (11): `dotnet test ZeroStrap.Tests` → 1151/1151 (+79 new: `GameKeyMatcherTests`, `SharedInputArbiterTests`, concurrency block in `MacroRuntimeTests`, `MacroOverlayLogicTests`, `MacroOverlayWindowTests`). UI smoke at 1120×700 (`smoke11.ps1`, backs up and restores `%LocalAppData%\ZeroStrap\Macros`): "E" and Tab captured as shortcuts, game-key hint and "Also used by … — both start together." note render, two macros run side by side (running dots), Stop stops only the selected one, Stop all enables/disables correctly, header fits one row, overlay settings render. NOT verified (no Roblox here): physical key passthrough into Roblox, chat pause in a real chat box, concurrent macros in-game, the overlay over Roblox (windowed, borderless and exclusive fullscreen), overlay corner/fade on screen.
- 2026-11-01 (10b): `dotnet test ZeroStrap.Tests` → 1072/1072. UI smoke at 1120×700 (`smoke10b*.ps1`): hand-built macro, Tab captured as a key, Timeline view renders lanes/blocks, Zoom in/Fit, click-select + Delete, camera summary + preview, action toolbar on one row. Smoke macro removed. NOT yet exercised in the app: timeline drag/edge-resize/rubber-band/keyboard shortcuts, Discard/Keep-editing prompt, draft restore on return, .zeromacro drop import, export default name, Ease in/out preview, Light theme.
- 2026-11-01 (10a): `dotnet test ZeroStrap.Tests` → 1034/1034. UI smoke at 1120×700: editor renders, add Wait/Camera, shortcut capture rejects `w` and accepts F7, Run pauses with "Roblox isn't focused", Stop → Idle. With the window minimised: F7 starts and toggles off, Ctrl+Shift+End stops. Smoke scripts `smoke10a*.ps1`. Smoke macro removed from the real store afterwards.
- Macro items NOT yet verified with real input: recording via real hooks/Raw Input (SendKeys input is injected, so the recorder ignores it by design), hook-removal recovery, playback into a live Roblox window, UIPI warning against an elevated window.
- 2026-11-01: `dotnet test ZeroStrap.Tests` → 531/531 pass, 0 build errors.
- 2026-11-01: dev build UI smoke at 1280×800 — Mods Studio tabs, sidebar badge, Overview mod count render correctly.
- 2026-11-01: import preview smoke at 1120×700 with a crafted zip (wrapper folder detected, `.lua` rejected, README ignored). Install not run against the real store (engine tests cover it).
- Not yet done: Phase 7 acceptance "real Roblox launch with mods on then off restores originals"; a real-world mod pack install in the UI; real 2 GB / 20,000-entry archives (tests use injected limits).
- UI smoke scripts: `smoke7b.ps1`, `smoke8b.ps1` pattern — dot-source `zsui.ps1`, `Start-Shell 1120 700`, click by AutomationProperties.Name (e.g. "Import mod", "Choose zip", "Cancel import").

## Hard rules (from the plan; do not re-ask)
- `ZeroStrap/FastFlags/*` and `FastFlagManager.cs` are a black box. Use `IFlagStore` / `FastFlagWorkspace.Instance.Store` only.
- Memory injection + watchdog toggles stay visible and unchanged. Generic Fast Flag editor stays (UI-only rework).
- Collections replace (never merge) after confirmation, with undo. Pure FPS placeholder resolved at apply time, never written literally.
- About: "Zero — Project Creator & Maintainer"; Claude = primary AI development assistant.
- `Bootstrapper.ApplyModifications` is unchanged; Mods Studio writes the merged overlay into `Modifications\` and tracks ownership. Never touches `ClientSettings\` or files it did not write.

## Key locations
- Mods engine: `ZeroStrap/Mods/*` (`ModStudio`, `ModPackageStore`, `ModOverlayBuilder`, `ModConflictResolver`, `ModPathRules`, `LegacyModMigrator`).
- Mods UI: `UI/Elements/Shell/Pages/ModsStudioPage.*`, `UI/Utility/ModsStudioService.cs`, `UI/ViewModels/Shell/ModsStudioLogic.cs`.
- Archive safety: `ZeroStrap/Utility/SafeArchiveExtractor.cs` (shared by theme import; Phase 8 extends it).
- Themes: `UI/Theming/*`. Shell: `UI/Elements/Shell/*`.
- UI automation: dot-source a copy of `zsui.ps1` (UIAutomation helpers; `Start-Shell`, `Find-Name`, `Click`, `Shot`). Dev exe opens the shell with `-settings`; close any installed ZeroStrap first (settings lock).

## Known issues / leftovers
- Macro Studio: absolute MouseMoveTo may land off on mixed-DPI multi-monitor setups. Invalid drafts survive page navigation (static, in memory) but are lost if the app closes; the shell can't veto navigation, so "Keep editing" only applies to switching macros inside the page. Ruler/labels show slashed zeros (app font).
- Macro engine notes: Macros live in `ZeroStrap/Macros/*`, UI in `MacroStudioPage.*`, `MacroStudioLogic.cs`, `MacroStudioService.cs`, overlay in `UI/Elements/Overlay/MacroOverlayWindow.*` + `UI/Utility/MacroOverlayHost.cs` + `MacroOverlayLogic.cs`. Hotkeys run on their own message thread (not the UI thread).
- Shortcuts (phase 11): Ctrl/Alt/Win combos and F1-F24/End/Home/Ins/Del/PgUp/PgDn stay on RegisterHotKey (global, captured). Everything else ("game shortcuts": letters, digits, Space, Tab, punctuation, ±Shift) is watched by a WH_KEYBOARD_LL hook on the hotkey thread that never swallows input; `GameKeyMatcher` fires on the physical down transition only, ignores injected input, needs Roblox in the foreground, matches modifiers exactly, and pauses from "/" until Enter/Esc (pressed in Roblox). Escape, Enter and "/" are reserved. The hook exists only while a game shortcut is registered. Macros sharing a shortcut get one registration (`macro:a,b`); a press stops the playing ones of the group, otherwise starts all (group toggle, not per-macro flip). The emergency stop and its extra-modifier variants are refused for macros.
- Concurrency (phase 11): up to 8 macros (`MacroRuntime.MaxConcurrentMacros`); each run has its own `MacroPlayer`, `PrecisionDelay` (a shared one serialises waits) and `SharedInputArbiter` client. Ownership is a set per input: OS Down only when no other client owns it, OS Up only when the last owner releases; a run drops all its ownership when it ends (`Client.ReleaseAllOwned`); Stop all / emergency stop / shutdown sweep the arbiter under the runtime gate. Refused Ups (UIPI) stay pending in the arbiter and are retried by the runtime timer. A 9th start returns `TooManyRunning` and stops nothing.
- Known limits: chat pause only knows "/" → Enter/Esc; opening the chat by clicking it does not pause, and closing it by clicking away keeps shortcuts paused until Enter/Esc. A key-up missed by the hook is recovered after 1.5 s. Windows may silently drop a LL hook that times out (no re-install yet). The overlay is a topmost window: it will not show over exclusive fullscreen (unverified either way). Macros, shortcuts and the overlay only work while the settings shell is open and Macro Studio was visited once (runtime lifecycle unchanged).
- Overview shows "Configuration: Unsaved changes" right after a fresh launch (pre-existing, not from 7b). Not investigated.
- Test packages `zs-test-alpha` / `zs-test-beta` from earlier UI testing exist in the real store `%LocalAppData%\ZeroStrap\Mods\Packages`; remove via Mods Studio once the owner agrees.

## Working method
- Opus designs/reviews; one implementation worker at a time (`savvy-medium` default, `savvy-careful` only for security/migration-critical code). Workers get only the files and spec excerpts they need.
- Targeted tests while building; full suite at each commit. One commit per phase.

## Next session — start here
0. Ask the owner for the phase 11 in-game checks: E/Shift+Q/Space/Tab shortcuts still type in Roblox and do nothing in Discord/browser; "/" chat pauses and Enter/Esc resumes; holding a shortcut key does not re-toggle; two macros holding W, stop one → W stays held; Stop all / Ctrl+Shift+End release everything; overlay appears bottom-left over windowed and borderless Roblox, is click-through, never steals focus, dims after ~4 s, brightens on start/stop, hides on Alt+Tab; behaviour in exclusive fullscreen.
1. Ask the owner to try the timeline interactively (drag to retime, edge-resize, rubber-band, Ctrl+wheel, Delete/Ctrl+D/Ctrl+G) and the manual macro checks (real recording + F9, playback in Roblox, elevated-window warning) if not yet reported.
2. All active phases (0–8, 10) are done; Phase 9 FPS Unlocker stays deferred. Decide with the owner on pushing `zerostrap-expansion` / opening a PR to `main` (nothing pushed yet).
3. Optional cleanup: remove test mod packages `zs-test-alpha` / `zs-test-beta` from the real store once the owner agrees; investigate the Overview "Unsaved changes" on fresh launch.
