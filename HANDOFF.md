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
| 10b Macro Studio timeline, camera inspector, import/export polish | — | **next** |

## Verification
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
- Macro Studio: action toolbar wraps Delete onto a second row at 1120 px; invalid (unsaved) macro edits are dropped on navigating away; absolute MouseMoveTo may land off on mixed-DPI multi-monitor setups; Tab can't be captured as a key. Fold into 10b.
- Macro engine notes: Macros live in `ZeroStrap/Macros/*`, UI in `MacroStudioPage.*`, `MacroStudioLogic.cs`, `MacroStudioService.cs`. Hotkeys run on their own message thread (not the UI thread). Typing keys (letters, digits, Space, arrows…) are rejected as shortcuts unless Ctrl/Alt/Win is added.
- Overview shows "Configuration: Unsaved changes" right after a fresh launch (pre-existing, not from 7b). Not investigated.
- Test packages `zs-test-alpha` / `zs-test-beta` from earlier UI testing exist in the real store `%LocalAppData%\ZeroStrap\Mods\Packages`; remove via Mods Studio once the owner agrees.

## Working method
- Opus designs/reviews; one implementation worker at a time (`savvy-medium` default, `savvy-careful` only for security/migration-critical code). Workers get only the files and spec excerpts they need.
- Targeted tests while building; full suite at each commit. One commit per phase.
