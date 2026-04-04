# ToSh VS Code Extension

This directory is the repo-owned source for the local ToSh VS Code extension.

The installed extension under `~/.vscode/extensions/` is treated as a synced build artifact, not the source of truth.

When the current workspace contains `src/Tosh.Lsp`, the extension prefers the real ToSh language server over the built-in fallback providers. If no server is available, the extension still provides lightweight completions, hover help, and document symbols locally.

## Syncing

From the repo root:

```bash
python3 scripts/sync_vscode_extension.py
```

The sync step also installs npm dependencies when needed so the copied extension is runnable immediately.

Normal `dotnet build` and `dotnet test` runs for `src/Tosh.Cli` also trigger this sync automatically unless disabled.

To disable the automatic sync for a build:

```bash
dotnet build -p:DisableToshVsCodeExtensionSync=true
```
