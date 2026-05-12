# Tōme — the TōSh Terminal Editor

**Tōme** is a small, modal terminal text editor that ships alongside
TōSh. It is built on the same `Tosh.Tui.Editing` primitives the shell's
REPL uses, so the editing model — buffer, cursor, selection, undo/redo —
behaves identically wherever you encounter it.

Tōme has two modes: **Edit** (the default, behaves like a normal
free-typing editor) and **Command** (vim-ish normal mode plus the `:`
command palette). `Esc` toggles into Command mode; `i`/`a`/`o`/etc.
return to Edit mode.

Tōme is its own binary (`tome`) and has no shell dependency at runtime.
The TōSh `edit` builtin invokes `tome` when `$EDITOR`/`$VISUAL` are unset
and `tome` is on `$PATH`.

> This document is the canonical user-facing reference for Tōme. It is
> kept in Markdown for now; a LaTeX/PDF rendition matching the layout of
> the TōSh Language Spec is planned.

---

## Contents

1. [Installation](#installation)
2. [Invocation](#invocation)
3. [Screen layout](#screen-layout)
4. [Editing model](#editing-model)
5. [Modes](#modes)
6. [Keybindings](#keybindings)
7. [Command palette (`:`)](#command-palette-)
8. [Shell bridge (`!`)](#shell-bridge-)
9. [Tabs](#tabs)
10. [Search](#search)
11. [Replace](#replace)
12. [Find in files (`:grep`)](#find-in-files-grep)
13. [Find files by name (`:find`)](#find-files-by-name-find)
14. [Fuzzy picker (`Ctrl+P`)](#fuzzy-picker-ctrlp)
15. [Cross-file replace (`:gsub`)](#cross-file-replace-gsub)
16. [Formatting (`:fmt`, format-on-save)](#formatting-fmt-format-on-save)
17. [Embedded REPL](#embedded-repl)
18. [Persistent undo](#persistent-undo)
19. [Reload-on-disk-change](#reload-on-disk-change)
20. [Goto](#goto)
21. [Workspaces](#workspaces)
22. [Mouse](#mouse)
23. [Multi-cursor](#multi-cursor)
24. [Prompts & filesystem completion](#prompts--filesystem-completion)
25. [Syntax highlighting](#syntax-highlighting)
26. [Diagnostics](#diagnostics)
27. [Bracket pairing & matching](#bracket-pairing--matching)
28. [Line decorations](#line-decorations)
29. [Clipboard integration](#clipboard-integration)
30. [Files and paths](#files-and-paths)
31. [Roadmap](#roadmap)

---

## Installation

Tōme is part of the standard install set. From the repository root:

```bash
buildtosh install           # tosh, tosh-lsp, tosh-mcp, tome
buildtosh install tome      # just the editor
buildtosh install all       # explicit form, same as the default set
```

Both forms produce `/usr/bin/tome` via the Arch `PKGBUILD`. The package
name is `tome` (not `tosh-tome`) to match the binary.

For local development, `dotnet run --project src/Tosh.Tome` launches the
editor without installing.

## Invocation

```text
tome                        # open with an empty unnamed buffer; if cwd holds
                            # exactly one *.tome manifest, auto-load it
tome FILE                   # open FILE (created on first save if absent)
tome DIR                    # open DIR as an ad-hoc single-folder workspace
tome workspace.tome         # load a workspace manifest (folders + tabs)
tome -h | --help            # print help and exit
tome -v | --version         # print version and exit
```

Tōme requires an interactive terminal. If stdin or stdout is redirected
it refuses to start with a clear error.

## Screen layout

```
┌──────────────────────────────────────────────────────────────┐
│ profile.tosh* | utils.tosh                                   │  tab bar (only when > 1 tab)
├──────────────────────────────────────────────────────────────┤
│  1 │ #!/usr/bin/env tosh                                     │
│  2 │ var greeting = "hello"                                  │  ← editor area
│  3 │ echo $greeting                                          │     (gutter | text)
│  · │ ~                                                       │
├──────────────────────────────────────────────────────────────┤
│ EDIT │ tome — profile.tosh [+]       L3:8  L42  sel:14  [1/2] │  status line
├──────────────────────────────────────────────────────────────┤
│ saved /home/komrad/.config/tosh/profile.tosh                 │  message line
└──────────────────────────────────────────────────────────────┘
```

- **Tab bar** appears only when more than one document is open. The
  active tab is highlighted (reverse-video); a trailing `*` marks an
  unsaved buffer.
- **Gutter** shows line numbers and a faint depth bar. The current line
  is rendered brighter.
- **Editor area** shows the visible viewport. Lines past EOF are drawn
  as a faint `~`.
- **Status line** shows the current mode (`EDIT` / `CMD`), the editor
  name, the current file (with `[+]` when modified), cursor location,
  line count, selection length when active, and tab counter `[i/N]`
  when more than one tab is open.
- **Message line** is used for prompts (open, save-as, search, goto,
  `:`) and one-shot feedback (`saved X`, `not found: Y`, etc.).

## Editing model

The buffer model is the same one used by the TōSh REPL line editor:

- **Cursor** is a `(line, column)` location, both 0-indexed internally
  and displayed 1-indexed in the status line.
- **Selection** is an anchored range. `Shift` plus any cursor-movement
  key extends the selection from the existing anchor; any movement
  without `Shift` clears it. Insertions, deletions, and pastes replace
  the selection when one is active.
- **Undo/redo** coalesces consecutive same-kind edits (e.g. typing a
  word) into one entry. History depth is bounded.
- **Tabs** insert four spaces (`    `). There is no soft-tab toggle yet.
- **Gutter colour** reflects the most severe diagnostic on a line — see
  [Diagnostics](#diagnostics).
- **Bracket pairs** typed in Edit mode auto-insert their closer with a
  few context rules — see [Bracket pairing & matching](#bracket-pairing--matching).

## Modes

Tōme starts in **Edit** mode and behaves like any free-typing editor.
Press `Esc` to drop into **Command** mode; the status line switches
from `EDIT` to `CMD`.

### Command mode

Movement keys (arrows, Home/End, PgUp/PgDn) keep working in Command
mode. The following vim-ish keys are also available:

| Key       | Action                                          |
|-----------|-------------------------------------------------|
| `h j k l` | Move by character / line                        |
| `0` / `$` | Line start / end                                |
| `w` / `b` | Move by word forward / back                     |
| `e`       | Jump to end of current word                     |
| `gg` / `G`| First / last line                               |
| `{` / `}` | Previous / next paragraph (blank line)          |
| `%`       | Jump to matching bracket (`( ) [ ] { } < >`)    |
| `H M L`   | Top / middle / bottom of viewport               |
| `Ctrl+D` / `Ctrl+U` | Scroll half page down / up            |
| `f<c>` / `F<c>` | Find char forward / back on line          |
| `t<c>` / `T<c>` | As above, but stop one before the char    |
| `;` / `,` | Repeat last `f`/`F`/`t`/`T` forward / back      |
| `*` / `#` | Search word under cursor forward / back         |
| `x`       | Delete character under cursor (or selection)    |
| `D` / `C` / `Y` | Delete / change / yank to end of line     |
| `dd` / `cc` / `yy` | Delete / change / yank entire line     |
| `p` / `P` | Paste after / before cursor                     |
| `u`       | Undo                                            |
| `Ctrl+R`  | Redo                                            |
| `i`       | Enter Edit mode at the cursor                   |
| `a`       | Enter Edit mode one column right                |
| `I`       | Enter Edit mode at line start                   |
| `A`       | Enter Edit mode at line end                     |
| `o`       | Open a new line below and enter Edit mode       |
| `O`       | Open a new line above and enter Edit mode       |
| `/`       | Start incremental search                        |
| `n`       | Repeat the last search                          |
| `:`       | Open the command palette                        |
| `!`       | Open the command palette prefilled with `!`     |

#### Operators and text objects

Tōme implements the standard vim operator-pending grammar:

```
<operator> <motion>            e.g.  dw   c$   y%   d}
<operator> <text-object>       e.g.  ciw  yi"  da(  cas
<operator><operator>           linewise:  dd  cc  yy
```

Operators: `d` (delete), `c` (change — delete + enter Edit mode),
`y` (yank). All three put the affected text on the clipboard. Yank
leaves the buffer unchanged.

Motions usable as operator arguments: `h l w b e 0 $ { } G %`.

Text objects compose `i` (inner — content only) or `a` (around — content
plus delimiters / trailing whitespace) with a selector:

| Selector    | Object                                            |
|-------------|---------------------------------------------------|
| `w`         | Word                                              |
| `"` `'` `` ` `` | String delimited by that quote                |
| `(` `)` `b` | Parenthesised group                               |
| `[` `]`     | Bracketed group                                   |
| `{` `}` `B` | Braced block                                      |
| `<` `>`     | Angle-bracketed group                             |
| `p`         | Paragraph (run of non-blank lines)                |
| `s`         | Syntax node (tree-sitter, falls back to word)     |

So `daw` deletes a word with its trailing whitespace, `ci"` replaces
the contents of a string literal, `yas` yanks the smallest enclosing
syntax node, and `d}` deletes through the next blank line.

`Esc` in Command mode clears any pending operator or prefix.

The `:mode edit` and `:mode command` palette commands also switch.

## Keybindings

### Movement

| Key                       | Action                                |
|---------------------------|---------------------------------------|
| `←` `→` `↑` `↓`           | Move by character / line              |
| `Home` / `End`            | Line start / end                      |
| `Ctrl+E`                  | Line end (Emacs habit)                |
| `Ctrl+←` / `Ctrl+→`       | Move by word                          |
| `PgUp` / `PgDn`           | Page by viewport height               |
| `Shift+` *any movement*   | Extend selection                      |
| `Alt+G`                   | Goto line (`line` or `line:col`)      |

### Editing

| Key                       | Action                                |
|---------------------------|---------------------------------------|
| `Enter`                   | Insert newline                        |
| `Tab`                     | Insert four spaces                    |
| `Backspace` / `Delete`    | Delete character (or selection)       |
| `Ctrl+Backspace`          | Delete word left                      |
| `Ctrl+Delete`             | Delete word right                     |
| `Ctrl+Z`                  | Undo                                  |
| `Ctrl+Y`                  | Redo                                  |
| `Ctrl+A`                  | Select all                            |
| `Ctrl+C` / `Ctrl+X`       | Copy / cut selection                  |
| `Ctrl+V`                  | Paste                                 |

### Files and tabs

| Key                       | Action                                |
|---------------------------|---------------------------------------|
| `Ctrl+S`                  | Save (prompts when buffer is unnamed) |
| `Ctrl+O`                  | Open into a new tab                   |
| `Ctrl+T`                  | New empty tab                         |
| `Ctrl+W`                  | Close tab (confirm if dirty)          |
| `Ctrl+PgUp` / `Ctrl+PgDn` | Switch to previous / next tab         |
| `Ctrl+Q`                  | Quit (confirm if any buffer is dirty) |

### Search

| Key                       | Action                                |
|---------------------------|---------------------------------------|
| `Ctrl+F`                  | Start incremental search              |
| `Ctrl+G`, `F3`            | Find next                             |
| `Ctrl+R` *(in find prompt)* | Toggle regex                        |
| `Ctrl+I` *(in find prompt)* | Toggle case-insensitive             |
| `Esc` *(in prompt)*       | Cancel; restore cursor                |
| `Enter` *(in prompt)*     | Accept current query                  |

Note: `Ctrl+R` outside a prompt is a **global** binding that toggles
focus on the [embedded REPL](#embedded-repl). The interactive
find/replace prompt is reached via `:s/pat/repl/[flags]` (see
[Replace](#replace)).

### Language services (`.tosh` only)

| Key                       | Action                                |
|---------------------------|---------------------------------------|
| `Ctrl+K`                  | Hover info at cursor                  |
| `Alt+D`                   | Jump to next diagnostic               |

Backed by the in-process `Tosh.LanguageServices` engine — the same
parser/binder that powers `tosh-lsp`. No subprocess is spawned.

## Command palette (`:`)

Press `:` in Command mode to open a one-line prompt at the bottom of
the screen. `Esc` cancels, `Enter` commits.

While the prompt is open:

| Key            | Effect                                                |
|----------------|-------------------------------------------------------|
| `Up` / `Down`  | Cycle through command history (256 entries, dedup'd) |
| `Tab`          | Complete the first token against the verb table, or  |
|                | a path against the filesystem for path-taking verbs  |
| `Backspace`    | Delete previous character                             |

### Editor verbs

Resolved against a hardcoded table — these run instantly without going
through the shell.

| Verb (aliases)              | Action                                              |
|-----------------------------|-----------------------------------------------------|
| `w` / `write` *[path]*      | Save (optionally to a new path)                     |
| `wq` / `x` *[path]*         | Save and quit                                       |
| `q` / `quit`                | Quit (confirm if any buffer is dirty)               |
| `q!`                        | Quit, discard all unsaved changes                   |
| `e` / `edit <path>`         | Open `<path>` in a new tab                          |
| `tabnew`                    | New empty tab                                       |
| `tabclose` / `bd`           | Close the current tab                               |
| `tabnext` / `tn`            | Switch to the next tab                              |
| `tabprev` / `tp`            | Switch to the previous tab                          |
| `goto` / `g <line[:col]>`   | Jump to a line (and optional column)                |
| `diag` / `d`                | Jump to the next diagnostic                         |
| `help` / `h`                | Show the verb list on the message line              |
| `mode <edit\|command>`      | Switch modes explicitly                             |
| `set <option> [value]`      | Toggle editor option (see [Formatting](#formatting-fmt-format-on-save)) |
| `s/pat/repl/[flags]`        | Substitute in the current buffer                    |
| `sub` / `substitute /pat/repl/[flags]` | Same, space-separated form               |
| `grep` / `rg [/flags] <pat>` | Find in files across the workspace (or cwd)        |
| `find` / `f <pattern>`      | Find files by name (glob or substring)              |
| `files` / `p`               | Open the fuzzy file/symbol picker (`Ctrl+P`)        |
| `gsub/pat/repl/[flags]`     | Replace across all files in the workspace           |
| `fmt` / `format`            | Format the current buffer (see [Formatting](#formatting-fmt-format-on-save)) |
| `repl <sub>`                | Manage the embedded REPL pane (see [Embedded REPL](#embedded-repl)) |
| `reload` / `e!`             | Reload the current buffer from disk (see [Reload-on-disk-change](#reload-on-disk-change)) |
| `workspace` / `ws <verb>`   | Workspace management (see [Workspaces](#workspaces))|

Any first token that is *not* an editor verb falls through to the
shell bridge — `:ls`, `:git status`, `:date` all work directly.

## Shell bridge (`!`)

`:!cmd` (or any unrecognized `:` verb) is executed by spawning a fresh
`tosh -c <cmd>` process. The transcript is shown in a dedicated
`*Output*` tab that is reused across invocations so it doesn't pile up:

```
$ git status
On branch master
nothing to commit, working tree clean
[exit 0]
```

Stderr, when present, is shown under a `─── stderr ───` divider. Tōme
parks the cursor in Command mode in the `*Output*` tab so you can
`bd` (close it) or scroll without typing into the transcript.

The shell bridge is suitable for non-interactive commands (`ls`, `git`,
`echo`, `cat`, …). Programs that require a TTY (pagers, `vim`, `less`)
will not work — Tōme is not a terminal multiplexer.

Resolution order for the `tosh` binary: a sibling next to the running
`tome` executable first, then `PATH`.

## Tabs

Each tab owns its own buffer, view (cursor + scroll), file path, syntax
colorizer, search history, and search toggles. Switching tabs does not
mutate any of those.

- `Ctrl+T` opens a fresh empty buffer.
- `Ctrl+O` opens a file *into a new tab* — it never replaces the current
  buffer. (To open in-place, close the current tab first with `Ctrl+W`.)
- `Ctrl+W` closes the active tab. Dirty buffers are confirmed first. If
  it is the only open tab, the buffer is replaced with an empty unnamed
  one rather than exiting.
- `Ctrl+Q` quits the whole editor, confirming once if *any* tab is
  dirty.

The tab bar renders only when there are two or more tabs.

## Search

`Ctrl+F` opens an incremental search prompt. The buffer cursor jumps
to the first match as you type; if no match is found the cursor stays
in place and the message line reads `not found: Q`. The search is
forward and wraps from the bottom back to the top of the buffer once.

While the prompt is open:

| Key      | Effect                                                       |
|----------|--------------------------------------------------------------|
| `Ctrl+R` | Toggle **regex** mode. The flag indicator becomes `[r]`.     |
| `Ctrl+I` | Toggle **case-insensitive** mode. Indicator becomes `[i]`.   |
| `Ctrl+G` | Jump to the next match from the current cursor.              |
| `Enter`  | Accept the query; the prompt closes, cursor stays at match.  |
| `Esc`    | Cancel; the cursor returns to where the search began.        |

When regex mode is active the query is parsed as a .NET regular
expression (`System.Text.RegularExpressions.Regex`). Bad patterns are
reported on the message line (`bad regex: …`) and the cursor does not
move.

Each tab remembers its own last-used query and flag state, so `Ctrl+G`
/ `F3` resumes the previous search after switching tabs.

## Replace

Tōme has three entry points for substitution; they all share one engine
and one flag vocabulary.

### Flags

| Flag | Meaning                                                          |
|------|------------------------------------------------------------------|
| `g`  | All matches per line (default: first match only)                 |
| `i`  | Case-insensitive                                                 |
| `e`  | Treat the pattern as a .NET regex (default: literal text)        |
| `c`  | Confirm each match interactively (`y` / `n` / `a` / `q`)         |

### Interactive replace prompt

Reachable via `:s` with no body, or by typing `:s/pat/repl/c` to walk
matches with confirmation. (`Ctrl+R` previously opened this prompt; it
now toggles the [embedded REPL](#embedded-repl) globally. Use `:s` for
the replace flow.) When `c` is set, each match shows
`replace [y/n/a/q]? "…" → "…"` on the message line:

| Key      | Effect                                  |
|----------|-----------------------------------------|
| `y`      | Replace this match                      |
| `n`      | Skip this match                         |
| `a`      | Replace this and all remaining matches  |
| `q` / `Esc` | Stop                                 |

### `:s/pat/repl/[flags]` — command-line form

The separator is whatever character follows `s`, so `:s|/foo/|/bar/|g`
works when the pattern contains slashes. Escape any literal separator
with `\\`. An empty replacement deletes the match.

```text
:s/TODO/DONE/                # first TODO on every line
:s/TODO/DONE/g               # every TODO on every line
:s/error/Err/ic              # case-insensitive, confirm each
:s/\\d+/N/eg                 # every run of digits → "N" (regex)
:s|/usr/local|/opt|g         # custom separator
```

Whatever was substituted is also remembered as the tab's last-search,
so `Ctrl+G` / `n` afterwards jumps to the *next* matching occurrence
(useful when you want to verify the result).

## Find in files (`:grep`)

`:grep [/flags] <pattern>` (alias `:rg`) walks the workspace folders
(or the current working directory when no workspace is loaded) and
collects every line that matches. Results land in a dedicated
`*Results*` scratch tab.

```text
:grep TODO                   # plain-text search
:grep /i todo                # case-insensitive
:grep /e ^\\s*func           # regex
:rg /ie ^\\s*FUNC            # combined
```

Only flags relevant to *search* are honored: `i` and `e`. The `g` and
`c` flags are meaningful only for replace.

### What gets searched

- All files under each workspace folder, recursively.
- Directories named `.git`, `.hg`, `.svn`, `node_modules`, `bin`,
  `obj`, `.vs`, `.idea`, `target` are always skipped.
- Any directory or file basename listed in the workspace `Exclude` set
  is skipped.
- Files larger than 4 MB, or whose first 4 KB contain a NUL byte, are
  treated as binary and skipped.

### `*Results*` tab

The results tab looks like this:

```
grep: TODO [i]
files scanned: 312  matches: 7
(press Enter on a match line to jump)

/home/komrad/projects/tosh/src/Tosh.Tome/TomeApp.cs
  142:13  // TODO: revisit gutter width clamp
  201:5   // TODO: factor out the colorizer registry

/home/komrad/projects/tosh/src/Tosh.Tui/Editing/TextBuffer.cs
   55:9   // TODO: surrogate-pair-aware cursor moves
```

Tōme drops into Command mode in the `*Results*` tab so the cursor can
move freely. Press **`Enter`** on any `line:col  text` row to open
that file and jump to the match. Pressing Enter on the bare path line
opens the file at `1:1`.

The `*Results*` tab is reused across invocations, so you never end up
with stacks of stale result tabs. `:bd` closes it.

## Find files by name (`:find`)

`:find <pattern>` (alias `:f`) walks the same set of files as `:grep`
but matches **filenames** rather than content. Two pattern flavors are
supported:

- A **glob** containing `*` or `?` is converted to a case-insensitive
  regex anchored to the basename (`*.cs`, `Test?.cs`, `*Theme*`).
- Anything else is treated as a **case-insensitive substring** against
  the basename (`tomeapp` matches `TomeApp.cs`).

```text
:find *.tosh                 # every .tosh file in the workspace
:f config                    # any file whose name contains "config"
:find Test?.cs               # Test1.cs, TestA.cs, …
```

Results are written to the same `*Results*` scratch tab used by
`:grep`, one absolute path per line. Press `Enter` on a path to open
that file in a new tab. The directory skip list and workspace
`Exclude` set are honored exactly as for `:grep`.

## Fuzzy picker (`Ctrl+P`)

`Ctrl+P` opens a centered modal picker that fuzzy-matches against
either workspace files (default) or the current buffer's document
symbols (when the query starts with `@`). Also reachable via the
`:files` / `:p` / `:fuzzy` palette verbs.

### Keys

| Key             | Effect                                        |
|-----------------|-----------------------------------------------|
| Any printable   | Append to query and refilter                  |
| `Backspace`     | Delete last char; on empty query, close       |
| `Up` / `Down`   | Move selection                                |
| `PgUp` / `PgDn` | Page selection                                |
| `Home` / `End`  | Jump to first / last match                    |
| `Enter`         | Accept: open file, or jump to symbol          |
| `Esc`           | Cancel                                        |

### Scoring

Matching is case-insensitive subsequence: every query character must
appear in order in the candidate. Bonuses are applied for matches at
the start of the candidate, immediately after a path/word separator
(`/`, `\`, `.`, `_`, `-`), and for consecutive runs. Ties prefer
shorter names. Currently-open file tabs are pulled to the top of the
file list so recent files surface first.

### File pool

The file pool is the same walker `:grep` and `:find` use: workspace
folders if a workspace is loaded, otherwise the current working
directory. The pool is capped at 5000 entries; `AlwaysSkipDirs` and
workspace `Exclude` apply.

### Symbol mode

With a query like `@foo`, the picker filters `GetDocumentSymbols`
results for the active buffer (only meaningful for `.tosh`). Picking
a symbol moves the cursor to its selection range start. Nested
symbols are flattened; the parent path is shown as a hint
(`method  in MyClass`).

## Cross-file replace (`:gsub`)

`:gsub/pat/repl/[flags]` runs the same substitution engine as `:s` but
applies it across every file the `:grep` walker would have looked at,
*writing changes back to disk*. The same separator rules apply.

```text
:gsub/TODO/DONE/             # rewrite first TODO per line in every file
:gsub/TODO/DONE/g            # rewrite every TODO in every file
:gsub/old-name/new-name/gi   # case-insensitive, all matches
:gsub/old/new/gc             # confirm per file (y / n / a / q)
```

When `c` is set, each file with matches surfaces a prompt:

```
gsub /home/.../foo.cs: 4 match(es) — apply? [y/n/a/q]
```

- `y` apply and continue
- `n` skip this file
- `a` apply this file and all remaining without confirming
- `q` / `Esc` stop immediately

A summary lands in the `*Results*` tab:

```
gsub: TODO → DONE [g]

     2  /home/komrad/projects/tosh/src/Tosh.Tome/TomeApp.cs
     1  /home/komrad/projects/tosh/src/Tosh.Tui/Editing/TextBuffer.cs

files scanned: 312  changed: 2  total replacements: 3
```

`:gsub` does not touch open Tōme tabs in memory — if a file you have
open is changed on disk, you'll see it after closing and reopening
the tab. (A reload-on-change hook is on the roadmap.)

## Formatting (`:fmt`, format-on-save)

Tōme can format the current buffer either on demand (`:fmt`) or
automatically on every save.

### Built-in dispatch

`.tosh` buffers are formatted by the in-process `ToshFormatter` (no
subprocess, no external dependency). For every other extension Tōme
looks up a built-in **external** formatter command and runs it as a
stdin/stdout filter with a 10-second timeout. Default mappings:

| Extension(s)                                | Command                            |
|---------------------------------------------|------------------------------------|
| `.rs`                                       | `rustfmt --emit stdout`            |
| `.go`                                       | `gofmt`                            |
| `.py`                                       | `ruff format -` (falls back to `black -`) |
| `.js` `.jsx` `.ts` `.tsx` `.json` `.md` `.css` `.html` `.yaml` `.yml` | `prettier --stdin-filepath {path}` |
| `.c` `.h` `.cpp` `.hpp` `.cc` `.hh`         | `clang-format --assume-filename={path}` |

The `{path}` token in a command template is replaced with the buffer's
filesystem path. A non-zero exit code surfaces the first line of
stderr on the message line and leaves the buffer untouched.

### Overriding

Set `TOME_FORMATTERS` to a colon-separated list of `ext=cmd` entries to
add or override mappings. The extension's leading `.` is optional.

```bash
export TOME_FORMATTERS='py=black -:zig=zig fmt --stdin:nix=nixpkgs-fmt -'
```

Overrides take precedence over built-ins. Unknown extensions are
reported as `no formatter for .ext` on the message line.

### `:fmt` — format now

`:fmt` (alias `:format`) runs the resolved formatter, replaces the
buffer contents on success, and preserves both the cursor position
and the undo history. Failures leave the buffer alone.

### `:set format-on-save on`

When format-on-save is enabled, every `:w` / `Ctrl+S` reformats the
buffer *before* writing to disk. The toggle is per-Tōme-process (not
persisted across runs). Aliases: `format-on-save`, `fmt-on-save`.

```text
:set format-on-save on       # enable
:set format-on-save off      # disable
```

Silent if no formatter is registered for the file's extension.

## Embedded REPL

Tōme can split the editor pane horizontally to host an in-process TōSh
REPL. The split occupies the bottom of the editor area only — the
explorer pane, tab bar, status line, and message line are unaffected.

Each REPL pane owns its own `ToshEngine` and runtime; opening the
pane sets the runtime's working directory to the active file's
directory (or `cwd` if the buffer is unnamed). Commands execute
synchronously while Tōme's render loop is paused; `Console.Out` and
`Console.Error` are redirected into the transcript, with stderr
rendered in red.

### Keys (REPL has focus)

| Key             | Effect                                                  |
|-----------------|---------------------------------------------------------|
| `Enter`         | Execute the current input line                          |
| `Up` / `Down`   | Cycle through input history                             |
| `PgUp` / `PgDn` | Scroll the transcript                                   |
| `Ctrl+L`        | Clear the transcript (history & engine state retained)  |
| `Ctrl+C`        | Clear the current input line                            |
| `Esc`           | Return focus to the editor (REPL stays visible)         |

### Toggle and verbs

`Ctrl+R` is the global toggle: it opens the pane if hidden, takes focus
if the pane is visible and unfocused, and returns focus to the editor
if the REPL already had focus. The pane status is also driven by
`:repl <sub>`:

| Subcommand         | Effect                                                |
|--------------------|-------------------------------------------------------|
| `:repl open`       | Open the pane and focus it                            |
| `:repl close` / `:repl hide` | Hide the pane (transcript & engine retained)|
| `:repl toggle`     | Same as `Ctrl+R`                                      |
| `:repl focus`      | Focus an already-open pane                            |
| `:repl <rows>`     | Resize the pane to `<rows>` rows (clamped to half the editor area) |

The pane prompt is `»`. The transcript is capped at 5000 lines (oldest
dropped). The minimum height is four rows; the default is ten.

### Limits

- Execution is synchronous. Long-running commands block the editor
  until they finish; there is no cancellation key today.
- Each pane's engine is independent of every other pane and of any
  `tosh` process the shell bridge spawns.
- Stdin is not connected — commands that read from stdin will see EOF
  immediately.

## Persistent undo

Tōme persists each buffer's undo and redo stacks across editing
sessions. When a file is reopened, its history is restored
transparently — `Ctrl+Z` walks back through edits made in *previous*
sessions just as it would through the current one.

### Storage

On save, Tōme writes a binary side-car keyed by a SHA-256 hash of the
file's absolute path. The first 16 hex characters of that hash become
the side-car's filename (`.undo` suffix). The side-car also embeds a
hash of the file's content at save time; restoration is gated on the
hash matching what's on disk, so editing a file externally invalidates
the stored history rather than producing a bogus replay.

Default location, in resolution order:

1. `$TOME_STATE_DIR/undo`
2. `$XDG_STATE_HOME/tome/undo`
3. `~/.local/state/tome/undo`

Each stack is capped at 256 frames (oldest dropped). Frames are stored
as length-prefixed UTF-8 line arrays plus a small cursor record.

### Opt-out

Set `TOME_NO_PERSISTENT_UNDO=1` to disable read and write of side-cars
entirely. Existing side-cars are left in place; remove the directory
manually to reclaim disk.

## Reload-on-disk-change

Tōme polls each open file's mtime + size roughly every 500 ms while
the editor is idle. When a file changes on disk:

- **Clean buffers** (no unsaved edits) are reloaded silently. The
  cursor is clamped to the new line/column bounds; the message line
  reads `reloaded from disk: <name>`.
- **Dirty buffers** are *not* clobbered. Tōme surfaces a one-shot
  warning: `file changed on disk; buffer is dirty — :reload to
  discard, :w to overwrite`. The warning fires once per external
  change, not every poll.

### Verbs

| Verb            | Action                                                |
|-----------------|-------------------------------------------------------|
| `:reload`       | Drop unsaved changes and reload from disk             |
| `:e!`           | Alias for `:reload` (vim-style)                       |

`:reload` works on a clean buffer too — useful for forcing a refresh
after `:gsub` rewrote the active file.

### Opt-out

Set `TOME_NO_WATCH=1` to disable the poll entirely. Files opened that
didn't exist at load time are not watched.

## Goto

`Alt+G` opens a small `goto line[:col]:` prompt. Examples:

```
42           → jump to line 42, column 1
42:10        → jump to line 42, column 10
1            → jump to the top
```

Values are clamped to the document: a line past EOF lands on the last
line; a column past EOL lands at end-of-line. Non-numeric input is
rejected with a `bad line number:` message.

## Workspaces

A **workspace** in Tōme is a small declarative description of a
project: a set of folder roots, an optional list of excluded basenames,
and the list of files to restore as tabs. Workspaces live on disk as
`.tome` files.

### Loading

| Invocation                  | Behavior                                  |
|-----------------------------|-------------------------------------------|
| `tome project.tome`         | Load that workspace                       |
| `tome path/to/dir/`         | Open the directory as an ad-hoc workspace |
| `tome` (no args)            | If cwd has exactly one `*.tome`, auto-load it |

When a workspace is loaded, the **explorer pane** opens along the left
edge with one node per folder root. Files matching `Exclude` and the
always-skip directory list (see [Find in files](#find-in-files-grep))
are hidden. `Tab` toggles focus between the explorer and the editor;
`Enter` on a file node opens it in a new tab.

### Verbs

| Verb                                  | Action                            |
|---------------------------------------|-----------------------------------|
| `:workspace open <path>`              | Load a `.tome` file               |
| `:workspace save [path]`              | Save (path required first time)   |
| `:workspace new <path>`               | Create a fresh empty workspace    |
| `:workspace add <dir>`                | Add a folder root                 |
| `:workspace info`                     | Print status to the message line  |
| `:workspace close`                    | Drop the active workspace         |

`:ws` is the short alias.

## Mouse

Tōme enables SGR mouse mode on startup. Set `TOME_NO_MOUSE=1` in the
environment to disable mouse handling (useful when SSH forwarding
mangles mouse events).

| Mouse action            | Effect                                              |
|-------------------------|-----------------------------------------------------|
| Click in editor         | Move the cursor to that screen position             |
| Shift+click             | Extend selection to that position                   |
| Alt+click               | Add a secondary caret at the click position         |
| Drag                    | Select range                                        |
| Click on a tab          | Switch to that tab                                  |
| Wheel up / down         | Scroll the buffer ±3 lines (cursor unchanged)       |

The explorer pane does not yet handle mouse input — use `Tab` to focus
it and arrow keys / `Enter` to navigate.

## Multi-cursor

Tōme carries a *primary* caret (the hardware terminal cursor) plus zero
or more *extra* carets that are rendered as reverse-video block cells.
Every edit at the primary fans out to every extra caret as a single
undo transaction.

### Keybindings (Edit mode)

| Keys                                | Effect                                                  |
|-------------------------------------|---------------------------------------------------------|
| `Ctrl+Alt+↑` / `Ctrl+Alt+↓`         | Add a caret on the line above the topmost / below the bottommost caret |
| `Alt+Shift+↑` / `Alt+Shift+↓`       | Same as above (fallback for WMs that eat `Ctrl+Alt`)    |
| `Alt+click`                         | Add a caret at the click position                       |
| `Esc`                               | Collapse extras (first press); enter Command mode (second) |

Motion keys (`←/→/↑/↓`, `Home/End`, `Ctrl+←/→`) fan out across every
caret. Selection-extending variants (`Shift+motion`) work too — each
caret grows its own selection.

### `:carets` palette verb

Available in Command mode as `:carets <sub>` (alias `:cursors`):

| Subcommand               | Effect                                                      |
|--------------------------|-------------------------------------------------------------|
| `:carets`                | Print the active caret count                                |
| `:carets count`          | Same                                                        |
| `:carets clear`          | Drop every extra caret                                      |
| `:carets above`          | Same as `Ctrl+Alt+↑`                                        |
| `:carets below`          | Same as `Ctrl+Alt+↓`                                        |
| `:carets line`           | One caret at column 0 of every line in the current selection |
| `:carets <pattern>`      | Seed a caret at every regex match in the buffer             |
| `:carets sel <pattern>`  | Seed a caret at every regex match within the selection      |

Patterns use .NET regex syntax. Zero-width matches are skipped to avoid
infinite seeding.

### Scope limits

- **Command-mode operators (`d` `c` `y`) target the primary caret only.**
  The operator/motion state machine has not yet been generalised across
  carets. Use Edit mode for multi-caret editing today.
- **Auto-pair insertion is disabled** when there are extra carets — typing
  `(` inserts a literal `(` at every site rather than the pair `()`.
- **Snapshot/undo collapses to the primary.** Undo restores the buffer
  state but does not rehydrate the extra-caret set.

### Deferred features

These are intentionally unbuilt and slated for a later session:

- `:'<,'>cmd` and `:<addr>cmd` line-address grammar (Ex-style ranges).
- `:!range!cmd` — filter a selection or range through a shell pipeline.
- `:norm <keys>` — replay an Edit/Command-mode key sequence at each
  caret (the closest existing equivalent to multi-cursor macros).

## Prompts & filesystem completion

The single-line prompt used by `open`, `save as`, `goto`, and the
dirty-confirm dialogs supports:

- `Esc` cancel, returning to the editor with the message line set.
- `Enter` accept the current value.
- `Backspace` delete the previous character.

The `open:` and `save as:` prompts additionally accept `Tab` for
filesystem completion. Completion behavior:

- Empty input expands to the current directory.
- A leading `~` is expanded to `$HOME`.
- The longest common prefix of matches is filled in; if there is a
  unique match and it is a directory, a trailing `/` is appended so
  you can immediately continue descending.
- `Tab` is a no-op when there are no matches (the message line is not
  changed; the cursor stays put).

## Syntax highlighting

Files ending in `.tosh` are highlighted by the same engine that powers
`tosh-lsp`: each render pulls semantic tokens from
`Tosh.LanguageServices.ToshLanguageFeatures.GetSemanticTokens`. This
gives parser-driven classification — commands, function names, type
names, variables, and keywords are distinguished by the AST rather
than per-line lexical heuristics. Doc comments (`## …`) render in
italics.

Tokens are recomputed whenever the buffer text changes (cached
otherwise) and selected text inside a colored span keeps the syntax
color underneath the reverse-video selection style.

Set `TOME_NO_LSP=1` to fall back to the simpler line-lexer colorizer
(useful if a parser change destabilises the editor mid-edit). Other
extensions render plain (no highlighting); adding one is a matter of
implementing `ISyntaxColorizer` and wiring it into
`TomeApp.ResolveColorizer`.

### Tree-sitter

Files with a registered tree-sitter grammar (currently C, Rust, Go,
Python, JavaScript, TypeScript, JSON, HTML, CSS, Bash) are parsed by
the bundled tree-sitter runtime. Reparses are **incremental**: on each
buffer change Tōme diffs old-vs-new text for a single bounding edit,
calls `ts_tree_edit` on the previous tree, then hands it back to
`ts_parser_parse_string` so unchanged subtrees are reused. This keeps
keystroke latency flat even in large files.

The `is`/`as` text objects ask the parser for the smallest named node
under the cursor, so `cas` rewrites a whole function call, string
literal, or block as a single operation.

### Theme & colour depth

Every coloured surface in Tōme — the three syntax colorizers, the
gutter, the current-line background, the completion popup, the status
bar, and the explorer pane — routes through a central
`TomeTheme` whose `Role` enum names each slot (`Keyword`,
`ControlFlow`, `GutterDiagError`, `PopupSelectedBg`, …). At startup
Tōme detects whether the host terminal supports 24-bit colour:

- `COLORTERM=truecolor` or `COLORTERM=24bit` → truecolor on.
- `TERM` ending in `-direct` or `-truecolor` → truecolor on.
- Anything else → fall back to the legacy xterm-256-color palette.

Set `TOME_NO_TRUECOLOR=1` to force the 256-color path on a terminal
that advertises 24-bit support. There is a single built-in dark theme;
its RGB values were picked to render visually identical to the legacy
indexed palette so neither path looks out of place. Config-file
theming, a light variant, and a runtime `:theme` switcher are not yet
wired up.

## Diagnostics

For `.tosh` files, Tōme runs the parser/binder on each frame whose
buffer text differs from the previous frame's. The result drives two
surfaces:

- The **gutter line number** turns red (error), yellow (warning), or
  soft blue (info/hint) for any line carrying a diagnostic. The most
  severe diagnostic on a line wins.
- `Alt+D` (or `:diag`) jumps the cursor to the next diagnostic at or
  after the current cursor position, wrapping to the first. The message
  line shows `[i/N severity] code: message`.

Diagnostics are cached against the buffer text — repeated `Alt+D` jumps
on an unchanged buffer don't re-parse. `TOME_NO_LSP=1` disables the
diagnostic surface (the gutter falls back to its plain colouring and
`Alt+D` reports nothing).

## Bracket pairing & matching

In Edit mode, typing an opener auto-inserts its matching closer and
leaves the cursor between them:

| Typed | Inserted | Notes                                                          |
|-------|----------|----------------------------------------------------------------|
| `(`   | `()`     |                                                                |
| `[`   | `[]`     |                                                                |
| `{`   | `{}`     |                                                                |
| `"`   | `""`     | Suppressed when the cursor is adjacent to a word character     |
| `'`   | `''`     | Suppressed when the cursor is adjacent to a word character     |
| `<`   | `<>`     | Only when the previous character is a word char (`x`, `_`, …); |
|       |          | leaves `<` literal in math/comparison expressions              |

Typing a closer (`)`, `]`, `}`, `>`) when the cursor sits directly
before the same closer character just moves the cursor past it
(type-over). Typing `"` or `'` when the next character is the same
quote does the same.

Bracket pairing is suppressed while a selection is active (the
selection-wrap behaviour is intentionally deferred).

A secondary highlight runs every frame: whichever bracket is at or
immediately before the cursor is rendered with bold + underline along
with its mate. The scan is depth-balanced and whole-buffer, but is not
yet string/comment-aware, so matches inside strings may resolve to a
mate outside the string.

## Line decorations

In addition to syntax colour, Tōme paints two ambient cues:

- **Current-line background.** The line containing the cursor is given
  a subtle dark-grey background that extends to the right edge of the
  viewport. The background composes with syntax colour and selection
  highlighting without leaking through resets.
- **Trailing-whitespace dim.** Trailing spaces and tabs on any line
  *other than the cursor's current line* render as a dim `·` (space) or
  `»` (tab). The cursor's own line is exempt so mid-typing whitespace
  doesn't flicker dots in and out.

Both decorations are purely visual — the underlying buffer is never
modified.

## Clipboard integration

Tōme attempts to talk to a system clipboard via, in order:

1. `wl-copy` / `wl-paste` (Wayland)
2. `xclip -selection clipboard`
3. `xsel --clipboard`

Each tool is given 500 ms to complete; failures are silently swallowed
so the editor stays responsive on headless systems. As a last resort,
Tōme falls back to an in-process string so copy/paste always works at
least within the same session.

There is no separate "primary selection" support; copy always targets
the clipboard.

## Files and paths

- Tōme writes files with UTF-8 encoding and LF line endings.
- `Ctrl+S` on an unnamed buffer prompts for a path with completion. The
  path is resolved against the current working directory of the `tome`
  process; the buffer's colorizer is re-resolved from the new extension.
- Saving over an existing file overwrites it without backup. Reading a
  non-existent path on `Ctrl+O` is not an error — it opens an empty
  buffer pre-named for that path, so the first `Ctrl+S` writes it.

## Roadmap

Items below are not yet implemented but are likely additions; this
section is informative, not normative.

- **Code actions** — `Alt+.` to apply `GetCodeActions` quick-fixes.
- **Block / column selection** from the keyboard (rectangular regions).
  *Multi-caret editing is shipped — see [Multi-cursor](#multi-cursor).*
- **String/comment-aware bracket match** (the current scan ignores them).
- **Command-mode line-address grammar** (`:'<,'>cmd`, `:<n>,<m>cmd`),
  `:!range!cmd` filter-through-shell, and `:norm <keys>` (replay an
  Edit/Command sequence at each caret).
- **Soft-tab toggle** and configurable tab width.
- **More tree-sitter grammars** beyond the built-in set (loaders for
  `.so` parsers at runtime).
- **Configurable keybindings** (`config.tosh`-backed).
- **Mouse support in the explorer pane**.
- **Primary-selection support** on X11.
- **Status of unsaved changes per tab** in the tab bar via color rather
  than `*`.
- **Persistent format-on-save toggle** across sessions (currently
  per-process only).
- **Async REPL execution** with a cancellation key (currently sync).

---

## Appendix: Source layout

| File                                              | Purpose                                       |
|---------------------------------------------------|-----------------------------------------------|
| `src/Tosh.Tome/Program.cs`                        | Entry point, argv handling                    |
| `src/Tosh.Tome/TomeApp.cs`                        | Render loop, Edit-mode key dispatch, tabs     |
| `src/Tosh.Tome/ModalCommand.cs`                   | Command-mode keys, `:` palette, shell bridge  |
| `src/Tosh.Tome/SearchReplace.cs`                  | `:s`, `:grep`, `:find`, `:gsub`, *Results* tab |
| `src/Tosh.Tome/FuzzyPicker.cs`                    | Ctrl+P picker (files + document symbols)      |
| `src/Tosh.Tome/DiskWatch.cs`                      | Reload-on-disk-change poll loop               |
| `src/Tosh.Tome/ReplPane.cs`                       | Embedded TōSh REPL pane                       |
| `src/Tosh.Tome/Formatter.cs`                      | `:fmt` dispatch (built-in + external)         |
| `src/Tosh.Tui/Editing/PersistentUndoStore.cs`     | SHA-256-gated undo side-car (XDG state)       |
| `src/Tosh.Tome/CommandMotions.cs`                 | Operators, motions, text objects (vim grammar)|
| `src/Tosh.Tome/TreeSitter/`                       | tree-sitter P/Invoke, incremental reparse     |
| `src/Tosh.Tome/WorkspaceCommands.cs`              | `:workspace` verbs and CLI workspace loaders  |
| `src/Tosh.Tome/ExplorerPane.cs`                   | Left-dock file tree                           |
| `src/Tosh.Tome/Workspace/`                        | `.tome` file format + in-memory model         |
| `src/Tosh.Tome/InputEvent.cs`                     | Mouse-aware input event model                 |
| `src/Tosh.Tome/LineRenderer.cs`                   | Per-line decoration compositor                |
| `src/Tosh.Tome/Tab.cs`                            | Per-document state (incl. diagnostics cache)  |
| `src/Tosh.Tome/TerminalDriver.cs`                 | Raw-mode ANSI I/O                             |
| `src/Tosh.Tome/GutterRenderer.cs`                 | Line-number + depth + severity gutter         |
| `src/Tosh.Tome/ToshSyntaxColorizer.cs`            | `.tosh` line-lexer fallback                   |
| `src/Tosh.Tome/LspBackedColorizer.cs`             | LSP-driven semantic colorizer (default)       |
| `src/Tosh.Tome/Clipboard.cs`                      | wl-copy / xclip / xsel shim                   |
| `src/Tosh.Tui/Editing/TextBuffer.cs`              | Shared buffer + selection model               |
| `src/Tosh.Tui/Editing/TextEditorView.cs`          | Viewport + cursor projection                  |
