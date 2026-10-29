# ZeroStrap v3.0.0 — Obsidian

First stable release of ZeroStrap, a fork of Bloxstrap rebuilt around the Obsidian design system.

## What changed

### New workspace
- Five-section shell: **Overview**, **Play**, **Configure**, **Appearance**, **System**
- Sidebar nav with the ZERO brand lockup; status bar with Save / Close
- Command palette (**Ctrl+K**) accepts section names (`fast flags`, `logs`, `theme`), verbs (`launch`, `save`, `about`) and theme switches (`theme dark`, `theme light`, `theme default`)

### Overview
- Time-aware greeting
- Roblox client hero with install path, version chip and one-click launch
- Three info cards (active profile, launch options, installation state)
- Four action tiles that navigate to Configure / Appearance / System / Play
- Right sidebar: live System Status, Quick Information, brand quote

### Play
- Launch hero wired to the real `LaunchHandler.LaunchRoblox` path
- Launch target selector and `ConfirmLaunches` toggle persisted to settings

### Configure
- Stats hero: configured Fast Flag count, active presets, custom integrations, mod files
- Primary **Open Fast Flag Editor** action
- Tabbed shell preserves the Bloxstrap Fast Flags editor, Mods manager, Integrations, Shortcuts and Behaviour pages verbatim — nothing lost from upstream

### Appearance
- Three theme cards (System default / Light / Obsidian) with swatches and live reskin via `WpfUiWindow.ApplyTheme`
- Custom bootstrapper theme card
- Expander hosting the advanced appearance panel from upstream

### System
- Diagnostics: Roblox Player, Roblox Studio and configuration path
- Logs: open log folder, copy latest log to clipboard
- Backups: snapshot `Settings.json`, `State.json` and the Fast Flags JSON to `<InstallBase>/Backups/ZeroStrap-yyyymmdd-hhmmss`
- Installation: open install folder, check for updates, uninstall (`keepData=true`)

### Component library (`UI/Components/ZeroControls.xaml`)
- `ZeroButton{,Primary,Secondary,Ghost}`, `ZeroIconButton`
- `ZeroToggle`, `ZeroDropdown`, `ZeroTextField`
- `ZeroNavItem` (sidebar radio template with selection stripe)
- `ZeroActionTileStyle`, `ZeroCommandPaletteStyle`, `ZeroStatusDotStyle` with semantic colours
- `ZeroSettingRowStyle`, `ZeroListRowStyle`, `ZeroMetaText`, `ZeroMonoText`

### Rebrand
- Project folder, csproj, sln, exe icon and all identifiers renamed from `Bloxstrap` to `ZeroStrap`
- 320 code files rewritten; upstream GitHub URLs repointed to this repo
- Models/`ZeroStrapRPC`, `ZeroStrapPage`, `ZeroStrapViewModel`, `IconZeroStrap{,Classic}.ico`
- Assembly metadata: `AssemblyName=ZeroStrap`, `RootNamespace=ZeroStrap`, `Product=ZeroStrap`, `Version=3.0.0`, `InformationalVersion=3.0.0-obsidian`

### Fast Flags
- Experimental toggle removed from the editor UI (hero row, dialog prompts, import blocking)
- `_workspace.ExperimentalMode = true` is hardcoded on page load, so every valid flag can be added
- `FastFlagManager`, `FastFlagWorkspace`, `FlagEditService`, `FlagSearchIndex`, `RobloxFlagAllowlist` are **unchanged** from the baseline — same storage, same validation, same memory-flag injection
- `BlockedExperimental` enum branches kept for completeness; neutralized at call sites

### License and attribution
- `LICENSE` preserved verbatim (MIT, Copyright (c) 2022 pizzaboxer)
- `README.md` credits the upstream Bloxstrap project

## Install

1. Download `ZeroStrap-3.0.0-obsidian-win-x64.zip` from this release
2. Extract to any folder
3. Run `ZeroStrap.exe`
4. If prompted, install the [.NET 6 Desktop Runtime](https://aka.ms/dotnet-core-applaunch?missing_runtime=true&arch=x64&rid=win11-x64&apphost_version=6.0.36&gui=true)

Windows SmartScreen may warn on first run because the binary is unsigned. Click **More info → Run anyway**.

## Build

```
dotnet build ZeroStrap.sln -c Release
```

## Known limitations

- No automated UI tests; functional verification is static (compile + code review)
- CI workflows reference SignPath / Winget secrets inherited from upstream; disable or configure them in repo settings if the fork does not use those services
- Verified on compile only — no live run of the launcher was performed in the release pipeline
