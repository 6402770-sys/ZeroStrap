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
| 8 Mods import (zip/folder/drag-drop + preview) | — | **next** |
| 9 FPS Unlocker | — | deferred (do not build) |
| 10a/10b Macro Studio | — | not started |

## Verification
- 2026-11-01: `dotnet test ZeroStrap.Tests` → 531/531 pass, 0 build errors.
- 2026-11-01: dev build UI smoke at 1280×800 — Mods Studio tabs, sidebar badge, Overview mod count render correctly.
- Not yet done: Phase 7 acceptance "real Roblox launch with mods on then off restores originals".

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
- Overview shows "Configuration: Unsaved changes" right after a fresh launch (pre-existing, not from 7b). Not investigated.
- Test packages `zs-test-alpha` / `zs-test-beta` from earlier UI testing exist in the real store `%LocalAppData%\ZeroStrap\Mods\Packages`; remove via Mods Studio once the owner agrees.

## Working method
- Opus designs/reviews; one implementation worker at a time (`savvy-medium` default, `savvy-careful` only for security/migration-critical code). Workers get only the files and spec excerpts they need.
- Targeted tests while building; full suite at each commit. One commit per phase.
