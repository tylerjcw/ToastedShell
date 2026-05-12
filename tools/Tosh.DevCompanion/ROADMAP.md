# Dev Companion — Roadmap

Token-efficiency and ergonomics improvements for the AI-facing memory store.
Ordered by **savings × effort**. Tier 1 is highest ROI; Tier 5 expands capability
once the basics are tight.

Status legend: `[ ]` planned · `[~]` in progress · `[x]` shipped.

---

## Tier 1 — Pure wins, low risk  *(shipped v1.1)*

- [x] **1. `memory_recall` returns excerpt by default.**
  - `mode: "brief" | "snippet" | "full"`, default `snippet`.
  - Uses `snippet(memories_fts, -1, '«', '»', '…', 16)`.
- [x] **2. Trim recall/list result envelope.**
  - `tags` dropped when empty; `scope` dropped when caller filtered to one;
    `created` is `YYYY-MM-DD` in non-verbose mode.
- [x] **3. Trim `memory_store` response to `{id, short_id}`.**
- [x] **4. Short IDs.**
  - `short_id` = first 8 hex chars; resolver accepts full id, short_id, or
    unambiguous prefix (≥4 chars).

## Tier 2 — Fewer round-trips  *(shipped v1.1)*

- [x] **5. `memory_store_batch`** — atomic batch with `relate: {to_id: "#0"}` sibling refs.
- [x] **6. `memory_update`** — metadata edits in place; content edits auto-supersede + tombstone.
- [x] **7. `memory_relate` accepts arrays** for `to_id`.

## Tier 3 — Capability gains  *(shipped v1.1)*

- [x] **8. Structured `links` field on entries.**
- [x] **9. `memory_tags` tool.**
- [x] **10. Dedup-on-store via content hash.**
- [x] **11. `pinned` flag** with FTS rank boost.

## Tier 4 — Init injection  *(shipped v1.1)*

- [x] **12. Auto-inject pinned project memories on `initialize`** into `serverInfo.instructions` (up to 6).

## Tier 5 — Graph + citations  *(shipped v1.2)*

- [x] **13. `memory_graph`** — BFS subgraph traversal around seed id(s), depth 0–3,
  optional `relationship` filter. Returns `{nodes, edges}` with content omitted
  unless `include_content=true`.
- [x] **14. `memory_open`** — resolve a memory's `links` into `file://…#Lstart-Lend`
  URIs, with optional `cwd` for relative paths.
- [x] **15. `memory_recall` filter by link target path** — new `links_path` param
  post-filters results to those citing a matching file path.
- [x] **16. `memory_list` decay filters** — `min_age_days` and `max_access_count`
  surface stale notes for audit (no auto-archive; user-driven cleanup).

---

## Out of scope (for now)

- Embedding-based semantic recall (column exists; FTS5 is sufficient).
- Cross-database federation.
- Per-user multi-tenancy (single-developer tool).
- Auto-archive of stale notes (Tier 5 surfaces the data; deletion stays manual).

## Possible future work

- **Tag aliases / merge** — collapse `tome` and `Tome` to one canonical form.
- **`memory_diff`** — show what changed between a memory and its `supersedes` parent.
- **Workspace-aware recall** — recall scoped to memories whose `links` cite the
  currently-open file, surfaced via a synthetic `context://current-file` query.
- **TUI memory browser** — small Tōsh-side `mem` command for human spelunking.
