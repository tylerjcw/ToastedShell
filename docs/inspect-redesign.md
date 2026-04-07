# Inline Interactive Object Inspector

## Overview

Redesign `inspect` from a static text dump into an inline interactive tree browser
rendered within the REPL scrollback (not fullscreen). Uses the existing
`IInlinePromptProvider` / `ConsoleInlinePromptProvider` infrastructure.

## Interface

```
ls | first | inspect          # default: inline interactive
ls | first | inspect -a       # include all members (non-public, static)
ls | first | inspect --flat   # legacy: static text output (current behavior)
```

Adds to `IInlinePromptProvider`:

```csharp
void Inspect(object? value, bool includeAllMembers = false);
```

## Rendering

Box-drawn inline widget, themed to match the shell's table style:

```
╭─ inspect: System.IO.FileInfo ─────────────────────╮
│  assembly: System.IO.FileSystem                    │
│  base: System.IO.FileSystemInfo                    │
│  value: FileInfo { Name = "foo.txt", Length = ... }│
│  path: FileInfo > Directory > Parent               │
├────────────────────────────────────────────────────┤
│ ▼ Properties (14)                                  │
│   Name          : string         = "foo.txt"       │
│   FullName      : string         = "/home/k..."    │
│   Length        : long           = 1234            │
│ › ▶ Directory   : DirectoryInfo                    │
│   Exists        : bool           = true            │
│   Extension     : string         = ".txt"          │
│ ▼ Methods (42)                                     │
│   CopyTo(string) -> FileInfo                       │
│   Delete() -> void                                 │
│   MoveTo(string) -> void                           │
│   ... (39 more)                                    │
│ ▶ Interfaces (3)                                   │
│ ▶ Items [8]                                        │
├────────────────────────────────────────────────────┤
│ selected: Directory : DirectoryInfo = /home/k/... │
│ status: 4/19 depth 1 path Properties > Directory  │
├────────────────────────────────────────────────────┤
│ ↑/↓ navigate  ←/→ collapse/expand  q quit         │
╰────────────────────────────────────────────────────╯
```

## Tree Structure

Top-level sections (each collapsible):

- **Header** — type name, assembly, base type, current value preview, breadcrumb (always visible, not collapsible)
- **Properties** — instance properties: name, type, value preview
- **Fields** — instance fields (if any)
- **Methods** — method signatures with parameter types and return type
- **Interfaces** — implemented interfaces
- **Items** — indexed elements (only for enumerables)

With `-a` / `--all`: also shows static members, non-public members.

## Node Types

Each tree node has: label, kind, collapsed/expanded state, depth, lazy children.

- **Section node** — "Properties (14)" — collapsible group header
- **Leaf node** — "Name : string = "foo.txt"" — no children
- **Expandable leaf** — "Directory : DirectoryInfo" — value is a complex object,
  expanding it reflects on that object and builds child section/property nodes
- **Ellipsis node** — "... (39 more)" — indicates truncation, expand to load more

## Key Bindings

| Key              | Action                                          |
|------------------|-------------------------------------------------|
| `↑` / `k`       | Move cursor up                                  |
| `↓` / `j`       | Move cursor down                                |
| `→` / `Enter`   | Expand collapsed node                           |
| `←`             | Collapse node, or jump to parent if collapsed   |
| `PageUp`         | Scroll up by page                               |
| `PageDown`       | Scroll down by page                             |
| `Home`           | Jump to first node                              |
| `End`            | Jump to last node                               |
| `i`              | Insert selected member text into the active REPL line at the cursor |
| `/`              | Filter nodes by name (type to search)           |
| `q` / `Escape`   | Exit inspector                                  |

## Lazy Expansion

Child nodes are not built until a node is expanded. When expanding a property
whose value is a complex object, reflect on it at that point. This keeps the
initial render fast and avoids infinite object graph traversal.

Depth limit: 4 levels. Circular references show `<circular>`.

## Breadcrumb

When drilled into nested objects, the header shows the navigation path:

```
path: FileInfo > Directory > Parent > Root
```

`←` at root level of a drilled-in object navigates back up.

## Child Inspection

Hitting "Tab" on a Child Object will begin inspecting the Child, with an option to return to the parent.
This would allow inspecting entire filled nested arrays in detail.

Hitting `i` on a property, field, method, item, or interface inserts that selection into the active REPL line at the cursor so the user can keep composing from what they just inspected. If no REPL line is active, the insertion is queued for the next prompt.

In the REPL, `F2` should open the inline inspector for the inspectable reference under the cursor when one can be resolved. `Alt+I` should provide the same action on terminals that do not surface function keys cleanly.

## Value Coloring

- Strings: green
- Numbers: cyan
- Booleans: yellow
- Null: dim red
- Type names: magenta
- Member names: bold

## Exit Behavior

On quit, the interactive widget is cleared and replaced with a compact static
summary (type name + member count) left in the scrollback, so there is a
record of what was inspected.

## Implementation

1. Define `InspectTreeNode` and `InspectTreeState` (tree model + visible node list)
2. Define `ObjectTreeBuilder` (reflection -> tree nodes, lazy)
3. Add `Inspect` method to `IInlinePromptProvider`
4. Implement in `ConsoleInlinePromptProvider` using existing ReserveLines/MoveUp pattern
5. Update `InspectCommand` to call `provider.Inspect()` when interactive
6. Keep `--flat` flag for the old static output path
