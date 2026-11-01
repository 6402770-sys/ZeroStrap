# ZeroStrap v3.1.0

A big feature release: a reworked Fast Flags page with ready-made collections, Theme Studio, Mods Studio and the new Macro Studio.

## Highlights

- **Macro Studio** — record a macro once or build it by hand, edit it in a list or on a timeline, and start it with a shortcut
  - Any key can be a shortcut (E, Shift+Q, Space, Tab…). Roblox still receives the key, shortcuts only act while Roblox is the active window, and they pause while the Roblox chat is open (**/** … **Enter**/**Esc**)
  - Up to **8 macros at once**, each started and stopped on its own; keys shared between macros are only released when no running macro still needs them
  - Two macros may share a shortcut (both start together); **Stop all** and the emergency stop (**Ctrl+Shift+End** by default) release every key
  - **Shortcut active** switch per macro; **Test** plays a macro once from ZeroStrap
  - Small **in-game overlay** over Roblox with your macros, their keys and whether they are running — click-through, never takes focus, dims when idle, corner and "running only" are configurable
  - Macros keep working after you close ZeroStrap: a small background process with a notification-area icon (**Stop playing macros**, **Turn off macros until next launch**) runs them for the Roblox session
  - Camera moves with easing, import/export of `.zeromacro` files
- **Fast Flags** — redesigned page and Fast Flag Editor workspace, plus **Zero FastFlags collections** you can apply (and undo) in one click
- **Theme Studio** — create and edit themes: colors, typography, corner radius, density, components, and image or video backgrounds; import and export themes
- **Mods Studio** — installed mods, a library and a conflicts view; import mod packs from a zip with a preview of what will be installed; ZeroStrap tracks the files it installs and only ever touches those
- **About** — redesigned About page

## Fixes

- No more false "update available" prompts
- Configure tabs and studio pages are reachable from the sidebar and the command palette

## Notes

- Some Roblox experiences don't allow automation. You're responsible for following each experience's rules and Roblox's Terms of Use.
- The in-game overlay is a normal always-on-top window: it shows over windowed and borderless Roblox, not over exclusive fullscreen.
- FPS Unlocker is not part of this release.

## Install

1. Download `ZeroStrap-3.1.0-win-x64.zip` below and extract it
2. Run `ZeroStrap.exe` (an existing install is updated in place)
3. If prompted, install the [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0)

The binary is unsigned, so Windows SmartScreen may warn on first run: **More info → Run anyway**.

## Credits

Based on [Bloxstrap](https://github.com/bloxstraplabs/bloxstrap) by pizzaboxer, MIT licensed — see `LICENSE`.
