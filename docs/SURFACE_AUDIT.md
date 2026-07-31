# TōSh Built-in Surface Audit

**Generated:** 2026-05-09 from `tosh --export-command-metadata` (255 commands).
**Status:** Initial audit pass (BACKLOG item §3 "Surface-Area Pruning"). Once
this document is reviewed, dispositions become the authoritative gate for
adding new builtins.

## Summary

| Disposition | Count | Meaning |
|---|---:|---|
| **Keep** | 196 | Long-term contract. Part of TōSh's stable surface. |
| **Fade** | 12 | Mark `obsolete`, document replacement, remove in a future major. |
| **Move** | 6 | Relocate to opt-in module (`tosh-interop` for native FFI). |
| **Consolidate** | 30 | Fold cluster behind a single command + subcommands or flags. |
| **Rename** | 11 | Pick canonical name; old name kept as fading alias. |

Cross-cutting recommendations:
1. **Native FFI → `tosh-interop` module.** Six `native-*` commands plus
   their short aliases (`alloc`, `read-buffer`, `write-buffer`, `size-of`,
   `offset-of`) are not safe defaults for a general-purpose shell
   (OWASP A04: Insecure Design — arbitrary memory ops by default).
2. **CLR introspection: prefer expression syntax.** Nine verb-style CLR
   commands (`call`, `call-method`, `get-prop`, `get-props`, `get-methods`,
   `set-prop`, `del-prop`, `has-prop`, `has-method`) duplicate fluent
   member-access syntax (`$obj.Method($args)`, `$obj.Prop`,
   `$obj.Prop = value`). Mark `fading`; keep `members` / `methods` /
   `describe-type` / `type-of` / `constructors` for structured
   introspection that has no fluent equivalent.
3. **Prompt segments → single `prompt` command with subcommands.**
   Eleven `prompt-*` builtins should fold to `prompt time`, `prompt git`,
   etc. Defer the breaking rename to a major bump; keep the audit row open.
4. **File handle IO: consolidate.** Fifteen file-handle primitives
   (`open-file`, `close`, `flush`, `position`, `seek`, `read-bytes`,
   `read-line-from`, `read-lines`, `read-from`, `read-to-end`,
   `write-bytes`, `write-file`, `write-to`, `write-line-to`,
   `append-file`) sprawl across both filesystem and stream-handle
   concerns. Recommend a single `io` namespace
   (`io.open`, `io.read`, `io.write`, `io.seek`, `io.close`) with
   the streaming `cat` / `lines` / `read-bytes` left as top-level for
   pipeline ergonomics.
5. **Math constructors live in Data, not Math.** `complex`, `mat`,
   `vec` are typed-constructor sugar and should move into the Math
   category to match user mental model.

---

## CLR (25)

| Command | Disposition | Rationale / Replacement |
|---|---|---|
| `call` | Fade | Use `$obj.Method($args)` (instance) or `Type.Method($args)` (static). |
| `call-method` | Fade | Same as `call`. |
| `cast` | Keep | No fluent equivalent; type coercion is a distinct operation. |
| `clone` | Keep | Object copy primitive; no fluent equivalent. |
| `constructors` | Keep | Structured introspection. |
| `del-prop` | Fade | Use `$obj.Prop = null` or remove from dict via `forget`. |
| `describe-type` | Keep | Structured type description; canonical. |
| `get-methods` | Fade | Use `methods $obj` or `members $obj`. |
| `get-prop` | Fade | Use `$obj.Prop`. |
| `get-props` | Fade | Use `members $obj` (filtered to properties). |
| `has-method` | Fade | Use `members $obj \| where _.Kind == "method" \| any _.Name == "X"`. |
| `has-prop` | Fade | Use `$obj.Prop != null` or `members $obj \| where`. |
| `load-assembly` | Keep | No fluent equivalent. |
| `members` | Keep | Canonical structured introspection. |
| `methods` | Keep | Canonical alongside `members`. |
| `native-alloc` (alias `alloc`) | Move | → `tosh-interop` module. Unsafe by default. |
| `native-free` | Move | → `tosh-interop`. |
| `native-offsetof` (alias `offset-of`) | Move | → `tosh-interop`. |
| `native-read` (alias `read-buffer`) | Move | → `tosh-interop`. |
| `native-sizeof` (alias `size-of`) | Move | → `tosh-interop`. |
| `native-write` (alias `write-buffer`) | Move | → `tosh-interop`. |
| `new` | Keep | Construction primitive (the `new Foo(...)` expression already covers most use; the command form supports dynamic type names). |
| `set-prop` | Fade | Use `$obj.Prop = value`. |
| `type-of` | Keep | Reflection entry-point. |
| `types` | Keep | Type listing. |

---

## Concurrency (12)

All keep. The set is internally consistent and recently designed.

| Command | Disposition | Notes |
|---|---|---|
| `async`, `await`, `spawn`, `scope` | Keep | Structured concurrency primitives. |
| `channel`, `channel-close`, `channel-recv`, `channel-select`, `channel-send` | Keep — but **consider** consolidating to `channel <subcommand>` (e.g. `channel.close`, `channel.recv`). Five of the twelve commands in this category begin with `channel-`; subcommands would compress noise without losing function. **Track for the next major.** |
| `race`, `settle`, `timeout` | Keep | Aspirational forms documented in BACKLOG; keep as canonical names. |

---

## Data (8)

| Command | Disposition | Notes |
|---|---|---|
| `as-file` | Keep | Adapter; widely used. |
| `complex` | Keep | But **recategorise** to Math (constructor). |
| `from`, `to` | Keep | Format adapters; canonical. |
| `hash` | Keep | Hashing primitive. |
| `mat` | Keep | Recategorise to Math. |
| `parse` | Keep | Adapter family. |
| `vec` | Keep | Recategorise to Math. |

---

## Filesystem (51)

This category mixes path utilities, directory-stack control, and
file-handle stream IO. Recommended split: keep path/directory commands as
top-level; move handle IO behind an `io` namespace.

### Path & directory operations — Keep

`basename`, `cd`, `chmod`, `chown`, `cp`, `df`, `dirname`, `du`,
`exists`, `find`, `findmnt`, `glob`, `is-dir`, `is-file`, `is-link`,
`length`, `ln`, `ls`, `lsblk`, `mkdir`, `mv`, `pwd`, `readlink`,
`realpath`, `rm`, `stat`, `touch`, `tree`.

### Renames

| Command | New name | Rationale |
|---|---|---|
| `mkdir-temp` | `mktemp -d` (or keep both) | `mktemp`-shaped naming matches GNU coreutils; `mkdir-temp` survives as alias. |
| `tempfile` | `mktemp` | Same — Unix users reach for `mktemp` first. |
| `copy-to` | (Fade) | Duplicate of `cp`; fade in favour of `cp`. |

### Directory-stack — Keep (shell-only)

`back`, `forward`, `dirs` — bash/zsh-equivalent `pushd`/`popd`/`dirs`.
Already correctly tagged `isShellOnly`.

### File-handle IO — Consolidate behind `io` namespace

| Existing | Proposed | Notes |
|---|---|---|
| `open-file` | `io.open` | Returns handle. |
| `close` | `io.close` | |
| `flush` | `io.flush` | |
| `position` | `io.position` | |
| `seek` | `io.seek` | |
| `read-bytes` | `io.read-bytes` | (kept top-level too — pipeline ergonomic.) |
| `read-line-from` | `io.read-line` | Drops "from" suffix; handle is positional. |
| `read-lines` | Keep top-level | Pipeline ergonomic. |
| `read-from` | `io.read` | |
| `read-to-end` | `io.read-all` | |
| `write-bytes` | `io.write-bytes` | (kept top-level too.) |
| `write-file` | Keep top-level | Convenience for path → bytes. |
| `write-to` | `io.write` | |
| `write-line-to` | `io.write-line` | |
| `append-file` | Keep top-level | Convenience for path → bytes. |
| `read-file` | Keep top-level | Convenience for path → string. Distinct from streaming `cat`. |

This is a **breaking rename** — defer to next major version. Track the
decision here so the surface doesn't keep accreting `*-from` / `*-to`
suffixes in the meantime.

---

## Functional (11)

All keep. The set is small, internally consistent, and well-named:
`compose`, `converge`, `curry`, `cycle`, `invoke`, `iterate`,
`partial`, `recur`, `repeat`, `repeatedly`, `unfold`.

---

## Math (1)

`round` only. `complex`, `mat`, `vec` should join this category once
the BACKLOG matrix/complex item ships. **No action now;** flag for the
Math expansion item.

---

## Network (3)

`http`, `ip`, `ping` — all keep. `ip` already uses subcommand model;
proven canonical.

---

## Pipeline (59)

The largest category, but the surface is justified by composition: every
verb is a distinct streaming primitive. **No fades or moves** recommended.

### Renames

| Command | New name | Rationale |
|---|---|---|
| `dedup` | (alias of `distinct`?) | Both currently exist; dedup keeps **adjacent** uniqueness, distinct keeps **global**. Both legitimate; rename `dedup` → `dedup-adjacent` for clarity, or document the contract loudly in `help`. **Track in streaming-contract docs item.** |

### Subcommand consolidation candidates

None recommended; verbs are too composable to put behind a namespace.

### Keep-list (54)

`all`, `any`, `average` (alias `avg`), `cartesian-product`, `chain`,
`chunk`, `collect`, `combinations`, `count`, `describe`, `distinct`,
`each` (alias `foreach`), `enumerate`, `filter`, `find-index`, `first`,
`flat-map`, `flatten`, `frequencies`, `get` (aliases `pick`, `select`),
`group-by`, `group-while`, `ignore`, `inspect`, `interleave`,
`intersperse`, `join`, `last`, `map`, `max`, `median`, `min`, `none`,
`parallel`, `partition`, `percentile`, `permutations`, `reduce`,
`rename`, `reverse`, `scan`, `skip`, `skip-until`, `skip-while`, `sort`
(alias `sort-by`), `stdev` (alias `stddev`), `step-by`, `sum`,
`summarize` (alias `summary`), `take-until`, `take-while`, `tee`,
`transpose`, `variance`, `where`, `window`, `xargs`, `zip`.

### Alias resolution

| Canonical | Fade | Note |
|---|---|---|
| `average` | `avg` (fading) | Already an alias; document `average` as canonical, keep `avg` indefinitely. |
| `each` | `foreach` (fading) | Same. |
| `get` | `pick`, `select` (both fading) | `pick`/`select` are LINQ/SQL-flavoured; fade in favour of `get`. |
| `sort` | `sort-by` (fading) | `sort -k key` should be the canonical form. |
| `stdev` | `stddev` (fading) | Pick one; `stdev` is shorter. |
| `summarize` | `summary` (fading) | Verb form is canonical for actions. |

These are **soft fades**: keep the alias forever, document the
canonical name, mark the alias `fading` so completion ranks it lower.

---

## Process (8)

All keep: `bg`, `fg`, `jobs`, `kill`, `lsfd`, `ps`, `signal`,
`wait-for`. Proven Unix shapes.

---

## Prompt (11) — Consolidate

| Existing | Proposed | Notes |
|---|---|---|
| `prompt-dir` | `prompt dir` | Subcommand. |
| `prompt-duration` | `prompt duration` | |
| `prompt-exit` | `prompt exit` | |
| `prompt-git` | `prompt git` | |
| `prompt-history` | `prompt history` | |
| `prompt-jobs` | `prompt jobs` | |
| `prompt-newline` | `prompt newline` | |
| `prompt-text` | `prompt text` | |
| `prompt-time` | `prompt time` | |
| `prompt-userhost` | `prompt userhost` | |
| `styled` | Keep | Different concern (output styling). Stays top-level. |

**Breaking rename.** Defer until a coordinated user-config migration
pass; keep `prompt-*` as fading aliases for one major version.

---

## Scripting (4)

All keep: `assert`, `format`, `raise`, `undef`. Tightly scoped.

---

## Shell (23)

| Command | Disposition | Notes |
|---|---|---|
| `apropos`, `clear`, `config`, `events`, `exec`, `exit` (alias `logout`), `help`, `hush`, `read-line`, `source`, `time`, `tui`, `ulimit`, `umask`, `view`, `which` (alias `whence`) | Keep | Core shell vocabulary. |
| `benchmark`, `dbg`, `unless`, `with-retry` | Keep (runes) | Macro forms; ship in `BuiltinRunes.cs`. |
| `debug` | Keep | Different from rune `dbg` — `debug` is the runtime debug-flag command; `dbg` is the inline expression-printer macro. **Document the distinction in help.** |
| `history`, `history-search` | Keep | History UX. |

---

## System (22)

All keep: `date`, `env`, `export`, `forget` (alias `unset`), `free`,
`guid`, `hostname`, `hostnamectl`, `id`, `journalctl`, `loginctl`,
`lscpu`, `lsipc`, `networkctl`, `seq`, `sleep`, `systemctl`,
`timespan`, `uname`, `uptime`, `vars`, `whoami`.

`unset` → `forget` was already resolved; `unset` is the fading alias.

---

## Text (17)

All keep. The cluster is balanced between Unix coreutils equivalents
and TōSh-shaped streaming forms.

| Command | Notes |
|---|---|
| `cut`, `grep`, `head`, `tail`, `tr`, `uniq`, `wc` | Unix parity — keep names verbatim. |
| `echo`, `lines`, `match`, `raw`, `replace`, `split`, `template`, `write`, `writeline`, `join-lines` | TōSh-shaped. Keep. |

The `write` / `writeline` / `echo` triple is intentional:
- `echo` — append newline, structured-display formatting (the shell default).
- `write` — no newline, raw bytes/string.
- `writeline` — newline, raw string.

Document this loudly in help to avoid confusion.

---

## Action Items

Ordered by leverage. Each item lands as a separate PR with conformance rows.

### Status (2026-05-10)

- ✅ **CLR verb-fade landed.** `call`, `call-method`, `get-prop`, `get-props`,
  `get-methods`, `set-prop`, `del-prop`, `has-prop`, `has-method` now carry
  `[CommandDeprecated("26.05.0.10")]` with notes pointing at the canonical
  syntax. Behaviour preserved.
- ✅ **`members` / `methods` got subcommands.** Both accept
  `has <name>` / `get <name>` filters; `members` additionally accepts
  `props` / `fields` / `methods` / `events` to slice the output by member
  kind. `props` and `funcs` are top-level shortcuts for the common cases.
- ✅ **`get` is now the canonical column-picker** (variadic field
  projection). `select` and `pick` remain as soft aliases.
- ✅ **`row` is the canonical row-picker.** Variadic on indices, list
  literals, and ranges (`row 7 8 9`, `row [3,1,0]`, `row 1..3`). Replaces
  the legacy positional behaviour previously layered into `get`.
- ⏳ **`io` namespace** — dropped from this round. `System.*` namespaces
  already work via the CLR resolver (`System.IO.File.OpenRead`, …); the
  spec + AGENTS now document `System.IO.*` as the canonical handle API.

### Remaining

1. **(Mechanical) Move six native-FFI commands behind a feature flag.**
   Either an explicit `tosh-interop` module that must be `import`-ed,
   or a `--allow-native-ffi` startup flag. Default off. Document in
   AGENTS.md security section.
2. **(Doc) Streaming/throughput contract** (BACKLOG §6) — uses this
   audit as the authoritative command list. Tag each Pipeline command
   lazy/eager/short-circuiting in its `help` output and cross-link.
3. **(Design) `prompt` subcommand consolidation.** Spec the migration
   path: introduce `prompt <segment>` as the canonical form, keep
   `prompt-*` as fading aliases, schedule removal for the next major.
4. **(Design) Alias-fade mechanism.** `RegisterAlias` currently has no
   "soft-deprecated" flag. Either extend the registry, or document the
   secondary aliases (`pick`, `select`, `foreach`, `avg`, `sort-by`,
   `stddev`, `summary`) as docs-only fading until a registry change
   lands. AGENTS.md should rank canonical first for LLM tooling.

After the verb-fade + introspection consolidation, the surface in the
default profile is **255 → 248** commands (3 new — `row`, `props`,
`funcs`; the 9 deprecated commands stay registered to avoid breaking
existing scripts but are tagged for removal). Native-FFI move would
take it to **~242**.
