# TōSh TUI Architecture

## Goal

Build a reusable terminal UI platform for TōSh that can support:

- `help browse` as a full-screen help and API browser
- future config, env, file, and job browsers
- scrollable detail pages, searchable lists, and split-pane layouts

The platform should be generic enough that the help browser is the first app, not a one-off implementation.

## Design Principles

1. Separate content from rendering.
   Help topics, config schemas, and filesystem objects should not know anything about terminal escape sequences or pane layout.

2. Separate app state from widgets.
   The TUI runtime should manage frames, key events, focus, and redraws. Individual apps should manage domain state.

3. Prefer a small number of reusable primitives.
   We should start with panes, lists, documents, scroll state, status bars, and search input before we think about editors or forms.

4. Grow from browsing into editing without replacing the runtime.
   The same runtime that powers `help browse` should also power a future config editor. Editing should add reusable field widgets, validation, and commit/cancel flows, not fork the TUI platform into a second system.

5. Reuse TōSh styling and config.
   TUI styling should flow from `$tosh.Config.Theme`, not invent a disconnected color system.

6. Make rendering deterministic and testable.
   Layout, scroll state, selection, and document rendering should be testable without a real terminal.

7. Grow from browse-first into editing through shared widgets.
   Browsing should still be the foundation, but editing and mutation should layer on reusable focus, form, validation, and confirm/picker widgets instead of introducing app-specific one-offs.

## Non-Goals For Phase 1

- a general form system
- mouse input
- inline shell execution inside the TUI
- a file manager or editor
- multiplexed panes or tabs

## Architecture Layers

### 1. Terminal Host

The terminal host owns:

- alternate screen entry/exit
- cursor visibility
- screen clearing and frame writes
- raw key input
- terminal size discovery

This layer should hide direct `Console.*` calls from higher-level TUI apps.

Suggested abstraction:

- `ITuiHost`
- `ConsoleTuiHost`

### 2. Core Geometry And Navigation State

These are pure model types with no direct console behavior:

- `TuiSize`
- `TuiRect`
- `TuiSplitLayout`
- `TuiScrollState`
- `TuiListState<T>`

These types should be reusable by the pager, help browser, config browser, and anything else we build later.

### 3. Frame And Document Model

The TUI runtime should render generic content, not help-specific content.

We should model:

- lists of rows
- rich document sections
- titles, subtitles, and status/footer text
- selected row state
- scroll position

The initial help browser can render a topic page as a document composed of sections like:

- synopsis
- usage
- arguments
- options
- examples
- notes
- related topics

### 4. App Runtime

Each TUI app should expose:

- initial state
- input handler
- frame builder
- optional commands/actions

Suggested shape:

- `ITuiScreen`
- `TuiScreenResult`

The runtime loop should:

1. read terminal size
2. ask the screen to build a frame
3. render the frame
4. read a key
5. dispatch it back to the screen
6. repeat until the screen exits

### 5. Shared Widgets

Phase 1 widgets should be minimal:

- split pane
- list view
- document view
- search box
- status bar

If we keep those clean, help/config/env/file browsers can all share them.

## Next Widget Layer

The next reusable layer makes editable apps possible without baking editing rules into one screen. Parts of this are already live through `config browse`, and the remaining work is to generalize them more cleanly:

- tree/list navigator
- key/value inspector
- field editor
- modal prompt / confirmation dialog
- filesystem picker
- collection editor state
- group editor state
- ordered-toggle editor state
- option-picker state
- path-editor state
- validation summary
- dirty-state status/footer
- aligned form-row / form-section layout

Those should be app-agnostic widgets. `config browse` should be the first serious consumer, not a special case.

## Config Editor Shape

`config browse` should be the first editable TUI app.

Suggested layout:

- left pane: config tree
- right pane: selected node detail
- bottom row: validation, dirty-state, and key hints

Suggested screen states:

- browse
- edit value
- confirm reset/revert/save
- validation/error view

Suggested reusable model:

- `TuiNodeId`
- `TuiTreeNode`
- `TuiFieldDescriptor`
- `TuiFieldValue`
- `TuiValidationMessage`
- `TuiDirtyChange`
- `TuiConfirmationDialogState`
- `TuiFilePickerState`

That model should be generic enough that it can later drive:

- config editing
- environment-variable editing
- plugin/app settings
- user-authored TUI apps

## Editing Rules

Editable widgets should not mutate live runtime state blindly.

The config editor should support:

- staged edits
- live preview where safe
- validation before commit
- revert/reset at node or subtree scope
- save/apply distinction where it matters

We should also preserve strong type information:

- booleans as toggles
- enums as option lists
- numeric values as constrained numeric editors
- text values as line editors
- paths as path editors with browse/complete hooks later
- color/style objects as structured nested editors, not raw strings only

## User-Facing TUI Goal

The long-term goal should not be “only builtin full-screen commands.”

We should design the TUI runtime so that user-facing screens are possible later through the same primitives:

- stable key event model
- screen/frame abstraction
- tree/list/document/form widgets
- theme integration through `$tosh.Config.Theme`
- deterministic rendering and testability outside a real terminal

That does not mean we need a public TUI plugin API immediately. It means the seams should stay general enough that exposing them later is realistic.

## Help Browser Shape

The first user-facing TUI app should be `help browse`.

Suggested layout:

- left pane: topic list
- right pane: topic detail page
- top row: query/filter
- bottom row: key hints and status

Suggested keybindings:

- `Up` / `Down`: move list selection
- `PageUp` / `PageDown`: scroll detail page
- `Left` / `Right` or `Tab`: switch focus between list and detail
- `/`: focus search
- `Enter`: open selected topic
- `b`: back
- `q`: quit
- `?`: show help

## Help Data Requirements

The TUI browser only becomes good if the help data is structured.

Each help topic should eventually carry:

- `Name`
- `Kind`
- `Category`
- `Summary`
- `Description`
- `Aliases`
- `Usage[]`
- `Arguments[]`
- `Options[]`
- `Examples[]`
- `InputContracts[]`
- `OutputDescription`
- `Notes[]`
- `Related[]`
- `SourcePath?`

That same model can later power:

- `help browse`
- `help <topic>`
- hover/signature help in the editor
- generated docs

## Integration With Existing TōSh Features

The new TUI layer should build on, not replace:

- the existing pager
- `StyledText`
- `$tosh.Config.Theme`
- the help topic model
- the current REPL input and display system

The pager is an existence proof that we can run alternate-screen full-screen terminal flows, but it should not become the long-term TUI architecture by itself.

## Rollout Status

### Phase 1 — DONE

- wrote the TUI architecture doc
- added core geometry (`TuiSize`, `TuiRect`), scrolling (`TuiScrollState`), selection (`TuiListState`), and host abstractions (`ITuiHost`, `ConsoleTuiHost`)
- added tests for those primitives

### Phase 2 — DONE

- added `ITuiScreen` / `TuiApplication` generic screen runtime with alternate-screen rendering
- added `TuiFrame`, `TextDocumentFormatter`, `TuiBoxDrawing`, and `TuiSplitLayout` rendering primitives
- first apps are read-only browsers

### Phase 3 — DONE

- implemented `help browse` as a full-screen two-pane browser
- CLR namespace tree with interactive navigation
- topic search and filtering
- formatted help rendering with Usage, Arguments, Options, Pipeline Input, Output, Examples sections

### Phase 4 — DONE

- implemented `config browse` as first editor-backed TUI app with auto-discovery, live preview, staged edits, validation, and commit/cancel
- `tui pick`, `tui filter`, `tui input`, `tui confirm`, `tui filepick` as standalone picker/input screens
- `tui run` for user-defined custom screens from `TuiScreen` definitions

### Phase 5 — DONE

- reusable editable widgets: `TuiCollectionEditorState`, `TuiGroupEditorState`, `TuiPathEditorState`, `TuiOrderedToggleEditorState`, `TuiFormLayout`, `TuiValidationMessage`
- `config browse` uses all of these for type-appropriate editing (color swatches, dropdowns, path editors, collection add/remove/reorder)
- widget/runtime seams stayed generic — all apps share the same `ITuiScreen` / `TuiApplication` runtime

### Phase 6 — DONE (added beyond original plan)

- inline prompt rendering for `tui pick --cli` and `tui filter --cli`
- themed box-drawn tables in the inline terminal (not alternate screen) matching `$tosh.Config.Theme.Tables`
- selection highlighting via `$tosh.Config.Theme.Tables.Selection`
- title, help, and search as proper span rows in table structure
- `tui confirm` and `tui input` inline rendering with box frames and dynamic width
- `IInlinePromptProvider` abstraction for testable programmatic access
- multi-line cell values automatically flattened for inline rendering

### What's Next

The TUI platform is feature-complete for its initial goals. Future TUI work is building new apps on the existing platform:

- `env browse` — environment variable browser/editor
- filesystem browsing
- job/process browsing
- user-authored TUI apps through the `tui run` + `TuiScreen` builder API

## What “Right On The First Try” Means Here

It does not mean we guess every future feature perfectly.

It means we choose the right seams:

- terminal host abstraction
- pure layout/state primitives
- reusable widgets
- structured content model
- app runtime separate from domain data

Those seams proved correct: `help browse` and `config browse` are very different apps sharing the same TUI runtime, and inline prompts layer on the same theme/widget model without forking the architecture.
