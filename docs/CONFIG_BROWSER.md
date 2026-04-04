# ToSh Config Browser Design

## Current Status

`config browse` is now live as an auto-discovered editable browser.

Current first-slice behavior:

- reflects the config tree automatically from `ToshConfig`
- keeps nested groups in a searchable expandable tree
- shows current values, staged values, and defaults in the detail pane
- uses a reusable form-row layout for aligned metadata, staged diffs, and action/status sections
- uses a reusable collection-editor state for selection, add/edit input, and apply/save/remove key flow
- uses a reusable group-editor state for section field selection and edit/toggle/raw-edit navigation
- uses a reusable ordered-toggle editor state for prompt-layout style module lists with toggle/reorder/commit flows
- uses a reusable option-picker state for enum and color-style selection lists
- uses a reusable path-editor state that combines text entry with filesystem-picker browsing
- supports staged editing for `bool`, enum, string, path, and number values
- supports structured group editing for sections with directly editable fields
- shows validation messages for invalid staged values like unsupported colors
- confirms before discarding dirty staged changes on quit through a reusable confirmation widget
- gives `ToshTextStyleConfig` groups a live style preview plus a structured style sub-editor
- gives color fields a named-color picker with live swatches, while still allowing raw text or hex entry
- gives path fields a path-aware text editor with resolved-path and existence metadata, plus a reusable filesystem picker for existing files and directories
- gives prompt-related config nodes a live success/failure prompt preview
- gives prompt layout fields a structured module editor with reorder/toggle controls, while still allowing raw text editing
- validates prompt layout strings against known prompt modules
- gives theme-focused sections richer visual previews for TUI, syntax, table, and prompt styling
- renders collection-shaped config values like display profile tables as real collection views in the detail pane
- supports structured collection editing for supported config tables like `Display.Profiles.Types`, including add, update, remove, apply, and save flows
- routes collection editing through reusable handlers so future simple ordered scalar list-like config values can plug in without another screen-level implementation
- shows subtree-level staged diffs so grouped edits are easy to review before apply/save
- gives startup-focused nodes browser-side startup actions for reload and initialization
- supports apply, revert, and reset-to-default at the field or subtree level
- supports saving staged changes back into the managed block in `config.tosh`

Current interactive controls:

- `Space`: toggle boolean values
- `e`: open the structured editor for the selected value or section
- `t`: raw-edit text-like values, including colors, paths, and prompt layout strings
- `b`: browse existing filesystem paths while editing a path value
- `n`: add a new item while inside a supported collection editor
- `a`: apply all staged changes
- `s`: save staged changes back to the managed config block
- `l`: reload startup config files from the current startup root when focused on `Startup`
- `i`: initialize missing startup files/directories when focused on `Startup`
- `r`: drop staged changes for the selected node or subtree
- `Delete`: remove the selected item while inside a supported collection editor
- `Shift+R`: stage default values for the selected node or subtree
- `Enter` / `Esc`: commit or cancel the active editor

That makes the browser useful today while still leaving room for broader collection-handler coverage, richer numeric editors, and even more specialized sub-editors.

## Goal

Build `config browse` as the first editable TUI app in ToSh.

It should let users:

- browse the live config tree
- search/filter config nodes
- inspect current values, defaults, and descriptions
- stage edits safely
- validate before applying
- reset values or whole sections
- save/reload startup config files

The config browser should also prove out the reusable widget model for future editable TUI apps.

## What We Already Have

The current TUI platform already gives us a strong start:

- full-screen alternate-screen runtime
- split-pane layouts
- list navigation and scrolling
- search box behavior
- styled borders/titles/theme integration
- a real help browser with grouped/tree-ish navigation

That means `config browse` does not need a brand-new TUI runtime.

## What We Still Need

### 1. Tree Navigation

The config surface is hierarchical:

- `Theme`
- `Display`
- `Repl`
- `Prompt`
- `Shell`
- `History`
- `Startup`

So we need a real tree state model, not just a flat grouped list:

- expandable/collapsible nodes
- node identity
- filtered tree visibility
- selection persistence while expanding/collapsing

Suggested reusable types:

- `TuiTreeState<TNode>`
- `TuiTreeNode`
- `TuiNodeId`

### 2. Field Editing

The config model is strongly typed, so the editor should be type-aware.

We need reusable field controls for:

- `bool`: checkbox/toggle
- enums: radio list / picker
- numbers: numeric input
- strings: text input
- paths: path-aware text editor plus filesystem picker
- nested objects: expandable group/section

Likely later:

- `ToshTextStyleConfig`: structured sub-editor
- color values: named-color picker plus raw text/hex editing
- broader list/collection editing coverage beyond the currently supported profile tables

Suggested reusable types:

- `TuiFieldDescriptor`
- `TuiFieldEditorKind`
- `TuiEditorState`
- `TuiTextInputState`

### 3. Staged Edits

The editor should not immediately mutate runtime state on every keystroke.

We need a staged edit buffer:

- original value
- staged value
- dirty flag
- revert per field
- revert per section
- apply staged changes

Suggested reusable types:

- `TuiDirtyChange`
- `TuiEditBuffer`
- `TuiEditSession`

### 4. Validation

Some fields have constraints:

- enums must parse
- numeric values may have ranges
- paths should be normalized
- colors/styles should be valid

We need validation hooks before apply:

- field-level messages
- section-level messages
- global validation summary

Suggested reusable types:

- `TuiValidationMessage`
- `TuiValidationSeverity`

### 5. Detail Pane Layout

The right pane should not just show raw object text.

For a selected config node, the detail pane should show:

- display name
- current path
- kind
- current value
- default value if known
- description/help text
- editor control
- related/reset/apply hints

This suggests a mixed document/form detail pane:

- read-only metadata sections
- active editor section
- validation section
- reusable validation summary/formatter

## Suggested Screen Layout

- top: search/filter row
- left: config tree
- right: detail/editor pane
- bottom: status, dirty-state, validation count, key hints

Example left-pane grouping:

- `Theme`
- `Display`
- `Repl`
- `Prompt`
- `Shell`
- `History`
- `Startup`

Inside each group, nested objects should expand as a tree:

- `Theme`
  - `Prompt`
  - `Syntax`
  - `Completion`
  - `Diagnostics`
  - `Tables`
  - `Tui`

## Field Presentation Rules

### Booleans

Use a checkbox/toggle-style editor:

- `[x] enabled`
- `[ ] enabled`

Keys:

- `Space`: toggle
- `Enter`: confirm staged value

### Enums

Use a picker/radio-style editor:

- inline option list when small
- popover/list selector when larger

Keys:

- `Enter`: open picker
- arrows: change selection

### Strings

Use a real text input state.

Needed behavior:

- cursor movement
- insert/delete
- home/end
- cancel/commit

### Numbers

Start with text-entry plus parse/validate.

Later we can add:

- small-step increment/decrement
- page-step increment/decrement

### Structured Style Objects

`ToshTextStyleConfig` is important enough to deserve a nested editor:

- foreground
- background
- bold
- italic
- underline
- dim

This should behave like a compact sub-form, not a raw CLR object dump.

### Prompt Layouts

Prompt layouts are also important enough to deserve a structured editor:

- show supported modules in their current order
- toggle module inclusion with `Space`
- reorder modules with `Shift+Up` / `Shift+Down`
- keep the raw string editable for advanced/custom layouts

That gives `config browse` a friendlier path for prompt composition without losing the low-level text form.

## Expand/Collapse Behavior

We should support:

- `Enter` on sections/nodes to expand/collapse
- `RightArrow` to expand or move into editing
- `LeftArrow` to collapse or move back out

The tree should preserve selection and scroll position across expands/collapses.

## Search Behavior

Search should match:

- node names
- full config paths
- maybe help/description text later

Filtered tree behavior should:

- keep matching nodes visible
- keep ancestor chain visible
- optionally auto-expand ancestor branches while filtered

## Apply Model

The current browser supports both runtime mutation and config-file persistence:

- stage changes
- apply to live runtime config
- save staged changes to the managed block in `config.tosh`
- revert staged changes
- reset node/subtree

Later slices can deepen that with:

- per-field save/apply affordances
- richer managed-block diff previews
- safer merge/update workflows for more complex config surfaces

## Recommended First Slice

This slice is now complete.

Build the smallest complete editable experience:

1. `config browse` screen shell
2. config tree navigation
3. detail pane with metadata
4. bool editor
5. enum picker
6. string/number text input
7. staged apply/revert
8. reset node/subtree

That already covers a large part of the config surface:

- `Display.Style`
- `Repl.GhostTextEnabled`
- `Theme.Tables.BoxStyle`
- `Theme.Tui.TreeStyle`
- history limits/file paths

## Defer For Phase 2

- broader collection handlers for more complex config collections beyond the current profile-table and simple scalar-list support
- richer numeric/enum picker affordances
- mouse support
- user-authored public TUI API
- generalized reusable form/document widgets extracted more cleanly from `config browse`

## Keybindings

Suggested initial bindings:

- `Up` / `Down`: move selection
- `Left` / `Right`: collapse/expand or move focus
- `Tab`: switch pane/editor focus
- `/`: focus search
- `Enter`: expand/open editor/commit, depending on context
- `Space`: toggle boolean / checkbox
- `e`: edit current value
- `t`: raw-edit the current text-like value
- `n`: add a collection item in supported collection editors
- `Delete`: remove the selected collection item
- `a`: apply staged changes
- `s`: save staged changes
- `r`: revert current field
- `R`: reset selected node/subtree
- `q`: quit

Dirty-on-quit confirmation is now implemented through a reusable confirmation dialog.

## Reusable Outcome

If we get this right, the same primitives can power:

- `config browse`
- `env browse`
- a future profile editor
- plugin/app settings
- eventually user-facing TUI apps
