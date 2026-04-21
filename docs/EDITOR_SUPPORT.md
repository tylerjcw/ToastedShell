# Editor Support

TōSh now keeps its VS Code extension source in-repo under [editor/vscode/tosh.tosh-lang](/home/komrad/projects/tosh/editor/vscode/tosh.tosh-lang).

## Current VS Code Support

- TextMate grammar / syntax highlighting
- language configuration for comments, brackets, and indentation
- snippets for common TōSh constructs
- a real `.NET` language server in [src/Tosh.Lsp](/home/komrad/projects/tosh/src/Tosh.Lsp)
- LSP-backed diagnostics, completions, hover help, document symbols, go-to-definition, and signature help when the workspace contains `Tosh.Lsp`
- fallback editor-side completions/hover/symbols when the language server is unavailable
- special variable support for names like `$tosh.Last.Result` and `_`
- repo-owned extension sync wired into normal `dotnet build` / `dotnet test` flows

## Current REPL Support

- syntax highlighting for keywords, commands, variables, types, strings, numbers, and punctuation
- runtime-aware syntax highlighting in the REPL for valid commands, invalid commands, and existing directory arguments
- dropdown-style tab completion for:
  - commands
  - external executables in command position
  - filesystem paths for shell-style path arguments like `cd`, `require`, and `ls`
  - scope variables and special variables
  - module names
  - user-defined classes and shell types
  - CLR namespaces, types, and members
- completion picker navigation with `Up` / `Down`
- completion acceptance with `Tab` / `Enter`
- completion dismissal with `Esc` or `q`
- ghost-text preview for the currently selected completion
- multiline continuation detection for open blocks, pipes, ternaries, and other unfinished expressions
- indentation suggestions for continuation lines
- word-wise cursor movement with `Ctrl+Left` / `Ctrl+Right`
- multiline vertical cursor movement with `Up` / `Down` before falling back to history navigation
- reverse history search with `Ctrl+R`

## Language Server Setup

The language server (`Tosh.Lsp`) ships as a self-contained native binary
published alongside the CLI.  The VS Code extension discovers it via the
`tosh.languageServer.serverPath` workspace/user setting.

```json
// tosh.code-workspace or settings.json
{
  "tosh.languageServer.serverPath": "/path/to/artifacts/publish/linux-x64/single-file/Tosh.Lsp"
}
```

If the setting is empty, the extension falls back to looking for
`src/Tosh.Lsp/bin/Debug/net10.0/Tosh.Lsp.dll` or the Release equivalent,
then to building the `.csproj` directly.

## MCP Server Setup

The MCP server (`Tosh.Mcp`) also ships as a self-contained native binary in
the same publish directory.  Register it with Claude Code via:

```bash
claude mcp add --transport stdio tosh -- \
  /path/to/artifacts/publish/linux-x64/single-file/Tosh.Mcp --stdio
```

Or add it to `.mcp.json` at the repo root:

```json
{
  "mcpServers": {
    "tosh": {
      "command": "/path/to/artifacts/publish/linux-x64/single-file/Tosh.Mcp",
      "args": ["--stdio"]
    }
  }
}
```

The MCP server exposes the same language-service backend as the LSP, plus a
`run_snippet` tool for executing ToSh code interactively.

## Building the Language Server and MCP Server

Both binaries are published as part of the normal build:

```bash
tosh scripts/build.tosh publish --no-install
```

This publishes `Tosh.Cli`, `Tosh.Lsp`, and `Tosh.Mcp` as self-contained
single-file native executables to `artifacts/publish/<rid>/single-file/`.

## Syncing the VS Code Extension

The installed local VS Code extension at `~/.vscode/extensions/tosh.tosh-lang-0.1.0` is synced from the repo copy.

Manual sync:

```bash
python3 scripts/sync_vscode_extension.py
```

The sync script also ensures the extension's npm dependencies are installed before copying it into `~/.vscode/extensions/`.

Automatic sync:

- `dotnet build src/Tosh.Cli/Tosh.Cli.csproj`
- `dotnet build Tosh.slnx`
- `dotnet test Tosh.slnx`

All of the above trigger the sync unless disabled with:

```bash
dotnet build -p:DisableToshVsCodeExtensionSync=true
```

## Current LSP Scope

The current server covers:

- parser diagnostics from the real TōSh parser
- completions for keywords, built-ins, special variables, current-document declarations, CLR imports, CLR members, user classes, and modules
- hover help for core language items and CLR-aware symbols
- semantic token highlighting
- document symbols for top-level declarations
- go-to-definition for visible declarations
- signature help for CLR and TōSh call sites

The next LSP milestones should focus on:

1. deeper member/type inference across more shell constructs
2. rename/refactor-style navigation features
3. keeping editor behavior in lockstep with new runtime syntax
