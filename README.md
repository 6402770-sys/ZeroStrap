# ZeroStrap

ZeroStrap is a third-party replacement for the standard Roblox bootstrapper, built around the Obsidian design system: a dark, minimal, independent launcher with Fast Flag management, mod support, custom themes and a redesigned shell.

ZeroStrap is a fork of [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap) by pizzaboxer, rebuilt with a new visual identity, a native component library and a consolidated five-section workspace (Overview, Play, Configure, Appearance, System).

ZeroStrap is only supported on Windows.

## Features

- **Overview** — Roblox client status, launch action and recent activity at a glance
- **Play** — one-click launch with per-run configuration
- **Configure** — unified Fast Flag editor, mods manager, integrations and shortcuts
- **Appearance** — theme picker (System / Light / Obsidian) and custom bootstrapper themes
- **System** — diagnostics, logs, configuration backups and installation controls
- **Command palette** — Ctrl+K from anywhere; type a section, a theme or a verb (`launch`, `save`, `theme dark`)

Underlying integrations preserved from upstream: Discord Rich Presence, mod loader, custom cursors / sounds / fonts, server location display.

## Installing

1. Download the [latest release](https://github.com/6402770-sys/ZeroStrap/releases/latest).
2. Run the installer and complete the setup wizard.
3. If prompted, install the [.NET 6 Desktop Runtime](https://aka.ms/dotnet-core-applaunch?missing_runtime=true&arch=x64&rid=win11-x64&apphost_version=6.0.36&gui=true).

Windows SmartScreen may warn on first run because the binary is unsigned. Click **More info → Run anyway**.

## Building from source

```
git clone https://github.com/6402770-sys/ZeroStrap.git
cd ZeroStrap
dotnet build ZeroStrap.sln -c Release
```

Output: `ZeroStrap/bin/Release/net6.0-windows/ZeroStrap.exe`.

## Credits

- Original Bloxstrap project and codebase: [pizzaboxer / bloxstraplabs](https://github.com/bloxstraplabs/bloxstrap) — MIT licensed, see [LICENSE](LICENSE).
- UI framework: [WPF-UI](https://github.com/lepoco/wpfui) (vendored under `wpfui/`).

## License

MIT. See [LICENSE](LICENSE) for the full text and attribution.
