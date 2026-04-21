# Line Editor RFC

Status: Draft  
Target: ToSh REPL line editor  
Date: 2026-04-20

## Summary

This RFC proposes a stability-first redesign of the REPL line editor around predictable editing behavior, explicit mode/state handling, and a clean separation between buffer model, keybinding resolution, and rendering.

The primary objective is to make the editor feel trustworthy under heavy daily use, especially for multiline input and completion-heavy workflows.

## Motivation

Recent fixes addressed two acute issues:

- multiline indentation growth across continuation lines
- accidental multiline buffer replacement during history navigation

These fixes reduced immediate friction but also highlighted that editor behavior is currently spread across multiple concerns in one flow. A stronger design is needed to avoid future regressions and to support advanced features (modal editing, richer completion controls, robust undo/redo) without destabilizing core interaction.

## Design Goals

1. Never lose user text unexpectedly.
2. Make all edit operations deterministic and reversible.
3. Keep multiline behavior consistent and unsurprising.
4. Keep completion and history interactions composable and predictable.
5. Support future keymap modes (default, emacs, vim-like) without rewriting core editor logic.

## Non-Goals

1. Implement full Vim compatibility in this RFC phase.
2. Introduce plugin architecture for editor behavior.
3. Replace REPL parser/classifier logic outside editor interaction boundaries.

## Core UX Guarantees

1. Draft safety guarantee: in-progress input is preserved unless user explicitly discards it.
2. Undo guarantee: every text mutation is captured in undo history as a transaction.
3. History safety guarantee: history browsing never clobbers multiline drafts.
4. Focus guarantee: overlays (completion/help/inspect insertion) cannot silently steal editor input state.

## Proposed Architecture

### 1. Buffer Model

Introduce a canonical `LineEditorBufferModel`:

- `Text` (single string, may contain newlines)
- `CursorIndex`
- `SelectionStart`, `SelectionEnd` (optional)
- `PreferredColumn` (for vertical movement)
- `Revision` (monotonic counter)

Mutations happen through named operations only:

- insert text
- delete range
- replace range
- move cursor (char, word, line, document)
- set selection

No direct ad hoc string rewrites in key handlers.

### 2. Edit Transactions and Undo/Redo

Introduce `EditTransaction` and `UndoStack` with coalescing rules:

- contiguous typing coalesces into one transaction
- cursor-only movements do not create undo entries
- completion acceptance creates one transaction
- multiline auto-indent insertion creates one transaction

Expose commands:

- `Ctrl+Z` undo
- `Ctrl+Y` redo

### 3. Keymap Resolution Layer

Introduce `IKeymapResolver` that maps `(mode, key chord, context)` to editor commands.

Context includes:

- overlay visibility (completion open/closed)
- multiline status
- selection present
- beginning/end of line

This isolates key semantics from buffer mutations and enables future keymaps.

### 4. Overlay Model

Treat completion/help overlays as read-only view state with explicit command hooks.

Rules:

- typing updates buffer first, then overlay refreshes
- `Esc` closes overlay first, then cancels broader mode only if overlay is already closed
- `Enter` behavior is mode-aware and explicit:
  - completion open: accept selected item
  - no completion: submit if syntactically complete, otherwise newline + continuation indent

### 5. Render Pipeline

Split rendering into `EditorRenderPlan`:

- prompt segments
- visible lines
- cursor screen position
- overlays

The render plan is derived from buffer state plus terminal dimensions, never from transient key events.

## Multiline Policy

### Continuation Indent Rules

1. Continue at current logical indent by default.
2. Increase one level only when the previous line ends with opener/operator continuation semantics.
3. Do not increase repeatedly solely due to open scope depth.

### Vertical Navigation Rules

1. `Up/Down` moves within multiline buffer while possible.
2. Only at top/bottom boundary can history navigation be considered.
3. Multiline buffers are never replaced by history unless explicitly requested.

Optional explicit history chord for multiline sessions:

- `Alt+Up` previous history
- `Alt+Down` next history

## History and Draft Handling

Introduce `DraftSlot` semantics:

- one active draft per prompt cycle
- history navigation snapshots/restores draft
- aborted history browse restores original draft automatically

Optional future extension:

- persisted draft recovery across crashes/restarts

## Completion Behavior Contract

1. Suggestions update incrementally from current buffer token context.
2. Selection changes are non-mutating until accepted.
3. Accepting completion mutates buffer exactly once.
4. Dismissing completion never mutates buffer.
5. Ghost text is view-only and never treated as real text until accepted.

## Implementation Plan

### Phase 1: Stability Foundation

1. Introduce buffer model + operation API.
2. Route existing key handlers through operation API.
3. Add undo/redo transactions.
4. Add multiline draft snapshot/restore around history navigation.

Acceptance criteria:

1. No known multiline clobber cases remain.
2. Undo/redo passes deterministic scenario tests.
3. Existing completion regression tests remain green.

### Phase 2: Interaction Consistency

1. Introduce keymap resolver.
2. Normalize overlay lifecycle and `Esc/Enter/Tab` behavior.
3. Add explicit history chords for multiline mode.

Acceptance criteria:

1. Key behavior matrix test suite covers all overlay states.
2. No ambiguous key outcomes for `Enter`, `Esc`, `Tab`, `Up`, `Down`.

### Phase 3: Power Features

1. Selection model expansion and text-object operations.
2. Kill/yank ring primitives.
3. Optional modal layer (vim-like) on top of shared operation API.

Acceptance criteria:

1. Modal keymap can be enabled without changing core buffer engine.
2. Core editor tests pass unchanged across keymap modes.

## Testing Strategy

### Unit Tests

1. Buffer operations and cursor invariants.
2. Undo/redo transaction coalescing.
3. Continuation indentation behavior.
4. History-draft preservation invariants.

### Integration Tests

1. REPL typing + completion accept/dismiss.
2. Multiline editing with vertical navigation and history boundary behavior.
3. Overlay interactions under key sequences.

### Property-Style Checks (where practical)

1. Cursor bounds invariant after every command.
2. Selection bounds invariant after every command.
3. Undo followed by redo returns exact original state.

## Risks and Mitigations

1. Risk: behavior drift during refactor.
   Mitigation: lock current expected behavior in tests before migrating internals.

2. Risk: performance regressions with large multiline buffers.
   Mitigation: render plan diffing and minimal redraw in follow-up optimization pass.

3. Risk: keybinding regressions across terminals.
   Mitigation: keep decoding and keymap layers separate; add explicit terminal compatibility tests for known escape/chord patterns.

## Open Questions

1. Should multiline history navigation require explicit modifier keys by default?
2. Should draft recovery persist across process restarts?
3. Should vim-like mode be built-in or behind experimental configuration?

## Proposed Initial Defaults

1. Keep current default keymap semantics where stable.
2. Add `Ctrl+Z`/`Ctrl+Y` for undo/redo once transaction engine lands.
3. Add explicit `Alt+Up/Alt+Down` history traversal in multiline mode.
4. Keep modal editing disabled by default.

## Appendix: Key Behavior Matrix (Initial)

| Context | Enter | Esc | Tab | Up/Down |
|---|---|---|---|---|
| No overlay, complete input | submit | clear selection/no-op | completion trigger or indent tab policy | move line/history boundary logic |
| No overlay, incomplete input | newline with continuation indent | clear selection/no-op | completion trigger or indent tab policy | move line/history boundary logic |
| Completion overlay open | accept completion | close completion | cycle/accept per setting | move completion selection |
| Help/inspect insertion pending | apply selected insertion if explicit | cancel insertion state | no-op or context command | navigate pending list/state |

This matrix is authoritative once Phase 2 begins; discrepancies should be treated as bugs.
