# Tōme Editor — Roadmap

Future gutter / editor-chrome work that didn't make this pass. Numbering
references the original brainstorm of 12 gutter improvements; items 1, 3,
5, 6, 7, 8, and 12 shipped in the same change set as this file.

## Deferred items

### 2. Folded-region indicator

Show a glyph (e.g. `▸`) next to a line that has a collapsed fold beneath
it. Requires a fold/region model in `TextBuffer` first — currently no
folding is implemented, only brace-depth tracking. Once a fold registry
exists, the gutter renderer can take a `IReadOnlySet<int> FoldedAt`
through `GutterContext` and emit the glyph in the marker column.

### 4. Current-line gutter highlight

Bleed the `CurrentLineBg` SGR background across the whole gutter (or just
the line-number cell) for the cursor's row. Today the line number colour
shifts to `GutterCurrentLine`, but the background stays default — a
proper highlight would make the active row pop more in busy diffs.

### 9. LSP code-action hint

When the active line has a code action available (quick-fix, refactor),
emit a lightbulb-ish glyph (`💡` if the terminal supports it, otherwise
`*`) in the right-marker column with priority below selection but above
search hits. Requires plumbing code-action availability from
`Tosh.LanguageServices` into `Tab` per line, ideally cached and
invalidated on edit + on LSP push.

### 10. Relative line numbers

Toggleable mode (e.g. `:set relativenumber on` or `:set rn on`) that
shows distance-from-cursor instead of absolute numbers, with the cursor
row continuing to show the absolute number. Cheap to implement once the
gutter renderer accepts the cursor row as input — currently it only
takes `isCurrentLine` per call.

### 11. Cache gutter render across frames

The gutter is recomputed line-by-line every frame. Most rows don't
change between frames; the renderer already has `_markers`/`_depths`
arrays it can extend into a full per-row cached SGR string keyed by
`(lineIndex, isCurrentLine, contextHash)`. Skip this until a profiler
says it matters — at ~209 lines a screen it's likely noise.

## Notes

- All entries here are nice-to-have polish, not bug fixes.
- If you pick one up, store a `decision` memory in the dev companion
  with the rationale before starting so future agents can find it.
