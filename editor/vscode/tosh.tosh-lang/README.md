# TōSh VS Code Extension

This directory is the repo-owned source for the local TōSh VS Code extension.

The installed extension under `~/.vscode/extensions/` is treated as a synced build artifact, not the source of truth.

When the current workspace contains `src/Tosh.Lsp`, the extension prefers the real TōSh language server over the built-in fallback providers. If no server is available, the extension still provides lightweight completions, hover help, and document symbols locally.

## Syncing

From the repo root:

```bash
tosh scripts/build.tosh sync
```

The sync step also installs npm dependencies when needed so the copied extension is runnable immediately.

Normal `dotnet build` and `dotnet test` runs for `src/Tosh.Cli` also trigger this sync automatically unless disabled.

To disable the automatic sync for a build:

```bash
dotnet build -p:DisableToshVsCodeExtensionSync=true
```

## Icon verification

The `.tosh` file icon is declared in `package.json` under `contributes.languages[].icon`
and uses `icons/tosh-light.svg` / `icons/tosh-dark.svg`.

The extension also contributes a `TōSh` terminal profile (`tosh.terminal`).
When VS Code creates that profile, `extension.js` passes the same light/dark SVGs
as the terminal `iconPath`.

After changing icons, run:

```bash
tosh scripts/build.tosh sync
dotnet build Tosh.slnx
sha256sum editor/vscode/tosh.tosh-lang/icons/tosh-*.svg ~/.vscode/extensions/tosh.tosh-lang-*/icons/tosh-*.svg
code --list-extensions --show-versions | rg '^tosh\.tosh-lang@'
```

Reload VS Code after syncing so its extension manifest and icon cache are refreshed.
Then open a `.tosh` file and create a terminal from the `TōSh` profile. If a
third-party file icon theme has its own `.tosh` icon or disables language
default icons, that theme can still override the language icon.
