# ZeroStrap v3.0.0 — Obsidian

First stable release of ZeroStrap, an independent Roblox launcher forked from Bloxstrap and rebuilt around the Obsidian design system.

## Highlights

- **New shell** — integrated title bar with a global command palette (**Ctrl + K**), network status, save (**Ctrl + S**) and window controls; compact sidebar with **Overview**, **Configure**, **Appearance** and **System**
- **Overview** — Roblox client card with the real installed version and one-click launch, Fast Flag / mod / installation summaries, live system status (client, configuration, network, disk), quick information, theme preview and a recent activity feed
- **Configure** — Fast Flags (including the Fast Flag editor), Mods, Integrations, Shortcuts and Behaviour in segmented tabs, with a stats strip on top
- **Appearance** — Obsidian / Light / System theme cards, a *Smooth animations* toggle, and the launcher window settings (style, icon, title, custom themes)
- **System** — logs, configuration backups, update check, install folder and uninstall, plus diagnostics for Player, Studio, configuration and disk
- **Motion** — page transitions, staggered entrances and hover feedback, all optional
- **Rebrand** — ZeroStrap name and icon everywhere; Experimental toggle removed (Fast Flag behaviour unchanged)

## Fixes

- The installed `ZeroStrap.exe` now starts (shipped as a single-file executable)
- System page no longer crashes on open
- "Fast Flag Editor" opens from the Fast Flags tab; duplicate button removed
- Hand cursor no longer shows over the whole tab content
- "Manage custom themes" now takes you to the theme manager
- Bootstrapper icon is sharp instead of pixelated
- No crash when launching a build whose version differs from the installed one

## Install

1. Download `ZeroStrap-3.0.0-obsidian-win-x64.zip` below and extract it
2. Run `ZeroStrap.exe`
3. If prompted, install the [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0)

The binary is unsigned, so Windows SmartScreen may warn on first run: **More info → Run anyway**.

## Credits

Based on [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap) by pizzaboxer, MIT licensed — see `LICENSE`.
