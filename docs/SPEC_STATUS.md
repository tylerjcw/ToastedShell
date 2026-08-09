# ToastScript Specification — Coverage Status

Status of [`docs/spec/toastscript-spec.tex`](spec/toastscript-spec.tex) and [`docs/spec/command-reference.tex`](spec/command-reference.tex) as of the most recent audit. This document records what is well-documented, what is mentioned-but-unexplained, and what is implemented-but-undocumented in the language.

It is intended as a *map* for future spec work, not a replacement for the spec itself.

**Last updated:** 2026-05-06 — Spec restructured into four parts (Language Core, Command Reference, Cookbook, Diagnostics) plus a new **Compilation** part documenting `--compile`, the tier model, profiles, type-discipline audit, public CLR shape, pipelines/redirections under compile, function references, MSBuild integration, the per-profile limit matrix, the conformance test layer, and compiler diagnostics. The duplicate "Appendix I: Command Reference" was removed (Section II is the canonical generated reference); the residual stub Appendices part was removed and the Diagnostic Code Reference was promoted to its own top-level part. Major spec gaps §1–§9 remain addressed; new Gap §10 tracks the Compilation part. Diagnostic code reference continues to be auto-generated from source via [`scripts/extract-diagnostic-codes.tosh`](../scripts/extract-diagnostic-codes.tosh).

---

## How to read this document

Each finding falls into one of three categories:

- **🟢 Documented.** The spec covers the feature with examples and prose.
- **🟡 Listed-only.** The spec mentions the feature (often only as a keyword in the keyword index) but does not explain its semantics.
- **🔴 Missing.** The feature exists in the implementation but is not mentioned in the spec at all.

File-path links go to the implementation. Spec line numbers reference [`docs/spec/toastscript-spec.tex`](spec/toastscript-spec.tex) unless otherwise noted.

---

## Spec coverage at a glance

| Chapter / Section | Spec line (approx.) | Status | Notes |
|---|---|---|---|
| Preface, Introduction | 381 | 🟢 |  |
| Syntax Fundamentals | 494 | 🟢 | Triple-quoted strings covered in "String Varieties" appendix. |
| Type System | 767 | 🟢 |  |
| **Refinement Types** | ~1175 | 🟢 | ✅ New section added 2026-04-29. See [Gap §1](#gap-1--refinement-types-and-coerce-mechanics). |
| Operators | ~1282 | 🟢 | ✅ `is in` substring notebox added; `**`, `//`, `..`, `...` index entries added. |
| Variables & Scope | ~1531 | 🟢 |  |
| Control Flow | ~1692 | 🟢 | ✅ `throw`-as-expression and pattern-matching forms documented. ✅ Postfix `if`/`unless` conditionals on jump statements documented (2026-05-07). |
| Functions | ~1956 | 🟢 | ✅ Doc-comment `@tag` section added. |
| Runes & Quoting | ~2234 | 🟢 with caveat | Verify "Quote" section covers the argument-position `quote { ... }` form. |
| Modules & User-Defined Types | ~2318 | 🟢 | ✅ `\ikw{}` macros added to all class/member modifier descriptions. See [Gap §2](#gap-2--class-member-modifiers). |
| Pipelines & Special Syntax | ~2795 | 🟢 |  |
| Configuration | ~3008 | 🟢 | ✅ `$tosh.*` namespace table expanded to 24 rows. See [Gap §7](#gap-7--tosh-runtime-namespace). |
| **Scripts and Subcommand Dispatch** | ~3165 | 🟢 | ✅ New chapter added 2026-04-29. See [Gap §8](#gap-8--subcommand-dispatch-system). |
| Built-In Command Catalog | ~3343 | 🟢 |  |
| Common Tasks | ~3370 | 🟢 |  |
| **Compilation (Part)** | ~3409 | � | New 12-chapter part added 2026-05-06; pending review. See [Gap §10](#gap-10--compiler--profiles). Covers CLI, profiles/tier model, type discipline, CLR shape, pipelines/redirections, function refs, MSBuild SDK, limits matrix, conformance layer, diagnostics. |
| Command Reference (Part) | ~3920 | 🟢 | Generated from runtime metadata via `tosh --export-command-metadata`. |
| **Diagnostic Code Reference (Part)** | ~end | 🟢 | ✅ Auto-generated from source; promoted from appendix to its own part 2026-05-06. See [Gap §9](#gap-9--diagnostic-code-reference). |

---

## Major gaps (in priority order)

### Gap §1 — Refinement types and `coerce` mechanics

**Status:** ✅ **Addressed 2026-04-29.** New `\section{Refinement Types}` added between the Type System chapter and the Operators chapter (spec line ~1175). Covers block form, all three clause types, inline form, parameter/variable annotations, four-phase execution order, diagnostics, and the generic-instantiation limitation.

<details>
<summary>Original gap description</summary>

**Was:** 🔴 entirely missing.

The keyword `where` is listed in the keyword index (used in comprehensions and in `let`/`where` filter clauses), but the type-refinement form is nowhere in the spec. This is the largest single gap.

**What needs documenting:**

- **Block form:**

  ```tosh
  type X = base {
      where <predicate>
      coerce <expression>
      if <guard> coerce <expression>
  }
  ```

- **Inline form:** `type X = base where <predicate>` and `type X = base where <predicate> coerce <fallback>`.

- **Refinement on parameters:** `func f(n: int where _ > 0)`.

- **Refinement on `var`:** `var x: int where _ > 0`.

- **The three coerce forms and their lifecycle:**

  | Form | When it fires | Purpose |
  |---|---|---|
  | `if <guard> coerce <expr>` | Before predicates, when guard is true | Pre-validation normalization (always-normalize) |
  | `coerce <expr>` (no guard) | Only after a predicate fails | Post-validation fallback (self-healing) |
  | `where <pred> coerce <fallback>` (inline) | Predicate first; fallback if pred fails | Combined predicate + repair |

- **Execution order** for a value being checked against a refinement type:

  1. All `if X coerce Y` clauses fire in order; when X is true, Y replaces the value.
  2. All `where` clauses evaluate against the (possibly-coerced) value.
  3. If any predicate fails, find an unconditional `coerce <expr>` clause; if one exists, run it and replace the value.
  4. Re-evaluate predicates against the coerced value. Pass → success. Fail → refinement fails.

- **Diagnostics:** `tosh.runtime.refinement_failed`, `tosh.runtime.refinement_requires_boolean`. The "which clause failed" marker (`→ where: …  (failed)`) was added recently and should be documented.

- **Limitations to mention explicitly:** generic instantiation of refinement types (`list<PathString>`) is *not* supported by the annotation resolver. Users wanting per-element validation must use `each _ as PathString`.

**Source files:** [src/Tosh.Language/RefinementAnnotation.cs](../src/Tosh.Language/RefinementAnnotation.cs), [src/Tosh.Language/ToshEngine.cs:7470-8022](../src/Tosh.Language/ToshEngine.cs#L7470-L8022), [src/Tosh.Language/Parsing/ToshParser.cs:4090-4280](../src/Tosh.Language/Parsing/ToshParser.cs#L4090-L4280).

**Recommended location:** new chapter between "Type System" and "Operators", OR a section within "Type System" titled "Refinement Types". The latter is cleaner since refinement types are a type-system feature.

</details>

---

### Gap §2 — Class member modifiers

**Status:** ✅ **Addressed 2026-04-29.** `\ikw{}` index macros added to all modifier descriptions in the Class Modifiers and Member Modifiers subsections. Prose was already complete.

<details>
<summary>Original gap description</summary>

**Was:** 🟡 listed-only; semantics not explained.

The keyword index (lines 539–625) lists modifier keywords like `fixed`, `lazy`, `fading`, `local`, `raw`, `vital`, `guarded`, `hermit`, `hollow`, `partial`, `strict`, `fluid`, `leaky`, `proud`, `overrule`, `once`, but the spec body does not explain what each modifier *does* in a class/struct/record/rune context.

**What needs documenting** — a reference table with one-line semantics, plus an example for each:

| Modifier | Applies to | Semantics |
|---|---|---|
| `shy` | property, method | Private (not externally visible). |
| `vital` | property | Required-on-construction. |
| `fixed` | property | Immutable after initialization. |
| `guarded` | property, method | Synchronized access wrapper. |
| `lazy` | property | Initialized on first access. |
| `fading` | property, method | Auto-cleanup on disposal. |
| `local` | property, method | Scoped visibility within the class. |
| `raw` | method | Bypass display profile when emitting results. |
| `hermit` | class | Isolated class (no implicit ambient access). |
| `strict` | class, record | No extra/dynamic properties. |
| `partial` | class, record | Definition split across files. |
| `fluid` | struct | Mutable fields (vs default immutable). |
| `sealed` | class, rune | Prevents inheritance / override. |
| `abstract` | class, method | Must be overridden. |
| `override` | method | Explicit override of a base method. |
| `static` | property, method | Type-level, not instance-level. |
| `shared` | property | Shared instance state. |
| `proud` | (verify) | (Listed in keyword index — semantics need tracing.) |
| `overrule` | (verify) | (Listed — needs tracing.) |
| `once` | (verify) | (Used by event handlers `is once`; verify other contexts.) |
| `leaky` | (verify) | (Listed — needs tracing.) |

The four marked `(verify)` need a code trace before documenting; they appear in the keyword list but their use sites should be confirmed.

**Source files:** [src/Tosh.Language/Parsing/StatementSyntax.cs:151-204](../src/Tosh.Language/Parsing/StatementSyntax.cs#L151-L204), [src/Tosh.Language/ToshClassDefinition.cs](../src/Tosh.Language/ToshClassDefinition.cs), [src/Tosh.Language/ToshClassPropertyDefinition.cs](../src/Tosh.Language/ToshClassPropertyDefinition.cs), [src/Tosh.Language/ToshClassMethodDefinition.cs](../src/Tosh.Language/ToshClassMethodDefinition.cs), [src/Tosh.Language/ToshStructDefinition.cs](../src/Tosh.Language/ToshStructDefinition.cs).

**Recommended location:** an appendix or a reference table within the existing "Class Definitions" section (line 2233), and a parallel table for struct/record-specific modifiers.

</details>

---

### Gap §3 — `is in` substring behavior

**Status:** ✅ **Addressed 2026-04-29.** Notebox added in the Membership Operators section documenting the string-as-haystack behavior for `is in`.

<details>
<summary>Original gap description</summary>

**Was:** 🟢 partially documented; emergent behavior unspecified.

The Membership Operators section (line 1289) documents `is in` as collection-membership and `contains` as "Collection contains value (also substring check for strings)". However:

- `is in` *also* performs substring-on-string testing — e.g. `".." is in "/foo/.."` returns `true`. This isn't mentioned.
- `$paths contains $dir` against `list<string>` returned silently false in testing. The spec says `contains` works on collections AND strings, so either the doc is aspirational, or there's a bug to verify.

**Recommended action:** verify `contains`-on-list behavior in the engine before documenting; if broken, file a runtime bug.

</details>

---

### Gap §4 — `throw` as expression

**Status:** ✅ **Addressed 2026-04-29.** Paragraph and three-example code block added in the Control Flow chapter after the `throw` statement section.

<details>
<summary>Original gap description</summary>

**Was:** 🟡 statement form documented; expression form not. Source: [src/Tosh.Language/Parsing/ArgumentSyntax.cs:84](../src/Tosh.Language/Parsing/ArgumentSyntax.cs#L84) (`ThrowArgumentSyntax`).

</details>

---

### Gap §5 — Pattern-matching syntax

**Status:** ✅ **Addressed 2026-04-29.** `\subsection*{Pattern Forms}` reference table added in the `match` section listing all 5 pattern forms, guard clause syntax, and arm-body forms.

<details>
<summary>Original gap description</summary>

**Was:** 🟡 `match`/`switch` documented at high level; pattern forms under-explained. Source: [src/Tosh.Language/Parsing/ArgumentSyntax.cs:113-129,157-161](../src/Tosh.Language/Parsing/ArgumentSyntax.cs#L113-L129).

</details>

---

### Gap §6 — Doc-comment `@tag` system

**Status:** ✅ **Addressed 2026-04-29.** New `\section{Doc-Comments}` added in the Functions chapter (before Visibility Modifiers) with a full tag reference table.

<details>
<summary>Original gap description</summary>

**Was:** 🟡 example-only. The spec showed `@param` and `@returns` in an example only.

Tags documented: `@param`, `@returns`, `@example`, `@deprecated`, `@see`, `@since`, `@throws`. Source: [src/Tosh.Language/Parsing/DocComment.cs](../src/Tosh.Language/Parsing/DocComment.cs).

</details>

---

### Gap §7 — `$tosh.*` runtime namespace

**Status:** ✅ **Addressed 2026-04-29.** Special Variables table expanded from 9 rows to 24 rows, grouped into 6 categories.

<details>
<summary>Original gap description</summary>

**Was:** 🟡 partial coverage in "Special Variables" appendix. Source: [src/Tosh.Language/ToshRuntimeNamespace.cs](../src/Tosh.Language/ToshRuntimeNamespace.cs).

</details>

---

### Gap §8 — Subcommand dispatch system

**Status:** ✅ **Addressed 2026-04-29.** New `\chapter{Scripts and Subcommand Dispatch}` added after the Configuration chapter, covering basics, arrow form, `flag`/`arg` declarations, nesting, all 5 modifiers (`eager`, `hidden`, `hollow`, `vital`, none), auto-help behavior, and a diagnostics table with 10 codes.

<details>
<summary>Original gap description</summary>

**Was:** 🔴 entirely missing. Keywords absent from keyword index: `subcommand`, `subcmd`, `flag`, `arg`, `eager`, `hidden`. Source files: [src/Tosh.Language/ToshEngine.Subcommands.cs](../src/Tosh.Language/ToshEngine.Subcommands.cs), [src/Tosh.Language/Parsing/ToshParser.cs:2114-2259](../src/Tosh.Language/Parsing/ToshParser.cs#L2114-L2259).

</details>

---

### Gap §9 — Diagnostic code reference

**Status:** ✅ **Addressed 2026-04-29.** Auto-generated appendix wired into the spec.

<details>
<summary>Implementation details</summary>

A ToastScript extractor at [`scripts/extract-diagnostic-codes.tosh`](../scripts/extract-diagnostic-codes.tosh) scans every `*.cs` file under `src/` for `"tosh...."` string literals and adjacent `Title`/`Help`/`Severity` fields. It emits three artefacts:

- [`docs/diagnostic-codes.md`](diagnostic-codes.md) — human-readable Markdown reference grouped by namespace.
- [`docs/spec/diagnostic-codes.tex`](spec/diagnostic-codes.tex) — LaTeX appendix included by the main spec via `\InputIfFileExists{diagnostic-codes}` between the Truthiness Rules and Command Reference chapters.
- `artifacts/diagnostic-codes.json` (optional, with `--json`) — structured manifest for downstream tooling (LSP, MCP, editor extensions).

As of the most recent run: **417 codes** across 7 namespaces (`runtime: 264`, `parser: 140`, `tui: 8`, `config: 2`, `get: 1`, `help: 1`, `history: 1`). Title extraction succeeds for ~94% of codes; the remaining ~6% use dynamic titles (variable references) and are marked _(see source)_ with a file:line link.

**Regenerate after adding new diagnostics:**

```bash
scripts/extract-diagnostic-codes.tosh
```

</details>

---

### Gap §10 — Compiler & profiles

**Status:** 🟡 **In flight (2026-05-06).** New `\part{Compilation}` added to the spec (~24 pages, 12 chapters) between the Cookbook and Diagnostics parts. The compiler had been entirely undocumented in the spec prior to this; only [`docs/COMPILED_TOSH.md`](COMPILED_TOSH.md) covered it. The new part is authored but pending review and may need follow-up passes as the compiler surface evolves.

<details>
<summary>What landed</summary>

| Chapter | Topic |
|---|---|
| Overview & Execution Model | Interpreter vs. compiler, pipeline (parse → bind → lower → type-check → emit), parity contract. |
| Command-Line Interface | Every `--compile` flag (`-o`, `--profile`, `--allow-dynamic`, `--emit-refasm`, `--no-apphost`, `--publish-single-file`) with examples and error messages. |
| Profiles & the Tier Model | Tiers 1/2/3 (native IL / runtime-hosted / source-replay), `permissive` / `runtime` / `pure` profiles, `RequireTier` semantics with deduplication. |
| Type Discipline | `tosh.compile.missing_type_annotation`, `tosh.compile.implicit_dynamic`, explicit `: dynamic` opt-out semantics, `--allow-dynamic` scope. |
| Public CLR Shape | Top-level statements, overloads, `[ToshOriginalName]` mangling, `[ToshType]` user-defined types, reference assemblies. |
| Pipelines & Redirections | IL emission for stages, `RunStage`/`DrainStatement`/`EmptyInput`/`SeedFromValue`, `BeginRedirection` scoping, nested-redirection invariants (round-3 fix). |
| Function References | Single-overload fast path, overload-set dispatch via `InvokeUserOverload`, `BindNamedArguments`, late-bound fallback. |
| Other Bound-Shape Emitters | Records (incl. spread, computed keys), tuple destructuring, `throw` as expression, helper inventory. |
| MSBuild Integration | `<Project Sdk="Tosh.Sdk">`, `ToshCompile` and `ToshStagePackageReferences` tasks, fallback path, NuGet consumption from C#. |
| Limits Per Profile | Full matrix of shapes × profiles. |
| Conformance Test Layer | `FeatureCases()` and `ConformanceCases()` rows in `CompilerFeatureMatrixTests`, console-serial collection requirement, recipe for adding a row. |
| Compiler Diagnostics | `tosh.compile.*` codes with cause/fix, tier-violation diagnostic shape, runtime callable diagnostics reachable only through compiled paths. |

**Source files referenced:** `src/Tosh.Compiler/CompileProfile.cs`, `src/Tosh.Compiler/BoundUnitEmitter.cs`, `src/Tosh.Compiler.Runtime/ToshHost.cs`, `src/Tosh.Sdk/Sdk/Sdk.{props,targets}`, `src/Tosh.Sdk.Tasks/ToshCompile.cs`, `src/Tosh.Runtime/ToshOriginalNameAttribute.cs`, `src/Tosh.Runtime/ToshTypeAttribute.cs`, `tests/Tosh.Tests/CompilerFeatureMatrixTests.cs`.

**Forward work:** When new shapes are emitted, add a matching `RequireTier` call, a `FeatureCase` row, and (if the shape produces observable output) a `ConformanceCase` row, then add the shape to the limits matrix in the spec.

</details>

---

## Undocumented words in the language

This section catalogs every *word* in the language — keyword, operator, modifier, soft keyword, syntactic form — that lacks documentation in the spec. Methodology: extract the parser's recognized tokens and the `help` system's canonical entry list, then cross-reference against the spec's `\ikw{}` / `\iop{}` index macros and prose sections.

### Soft keywords missing entirely from the spec

✅ All previously missing soft keywords are now documented (2026-04-29):

| Word | Added by |
|---|---|
| `coerce` | Gap §1 — Refinement Types section |
| `subcommand` / `subcmd` | Gap §8 — Subcommand Dispatch chapter |
| `flag` | Gap §8 |
| `arg` | Gap §8 |
| `eager` | Gap §8 |
| `hidden` | Gap §8 |

`vital` and `hollow` in the subcommand context are now covered by the new chapter.

### Words listed as keywords but with no prose explanation anywhere

✅ All addressed (2026-04-29) — `\ikw{}` macros and prose descriptions added in Member Modifiers subsection:

| Word | Status |
|---|---|
| `proud` | ✅ |
| `shared` | ✅ |

### Words explained in prose but missing index entries

✅ All `\ikw{}` macros added 2026-04-29 to Class Modifiers and Member Modifiers subsections:

| Word | Status |
|---|---|
| `vital` | ✅ | `fading` | ✅ | `fixed` | ✅ | `guarded` | ✅ |
| `hermit` | ✅ | `hollow` | ✅ | `local` | ✅ | `overrule` | ✅ |
| `partial` | ✅ | `public` | ✅ | `raw` | ✅ | `static` | ✅ | `strict` | ✅ |

### Operators with no `\iop{}` index entry

| Operator | Status |
|---|---|
| `=~` (regex match) | ✅ `\iop{=~}` already present in spec (Regex Operators section) |
| `!~` (regex non-match) | ✅ `\iop{!~}` already present in spec (Regex Operators section) |
| `..` (range) | ✅ `\iop{..}` added 2026-04-29 |
| `**` (exponent) | ✅ `\iop{**}` added 2026-04-29 |
| `//` (integer divide) | ✅ `\iop{//}` added 2026-04-29 |
| `...` / splat | ✅ `\iop{...}` added 2026-04-29 |

### Concept-words `help` surfaces but spec doesn't index

| Concept | Status |
|---|---|
| `null` | ✅ `\ikw{null}` added 2026-04-29 |
| `true` / `false` | ✅ `\ikw{true}\ikw{false}` added 2026-04-29 |
| `prop` | ✅ `\ikw{prop}` added 2026-04-29 |
| `interpolation` | 🟢 Indexed. |
| `redirection` | ✅ `\ikw{redirection}` added 2026-04-29 |
| `operators` | 🟡 Spec has chapter "Operators"; no top-level meta index entry. |

### Lowercase type aliases not enumerated in the type system chapter

✅ Added 2026-04-29 — the following were missing from the Built-in Type Aliases table:

| Alias | CLR Type |
|---|---|
| `array` | `System.Object[]` |
| `hashtable` | `System.Collections.Hashtable` |
| `table` | `ToSh.DataTable` |

`list`, `dict`, `set`, `tuple` were already present. The table now also includes clarifying descriptions ("Growable ordered list", "Immutable positional tuple").

### Auto-generated command reference: missing entries

- **`source`** — ✅ Full metadata attributes added to `SourceCommand.cs` 2026-04-29 (`[CommandCategory]`, `[CommandLongDescription]`, `[CommandArgument]`, `[CommandExample]`, `[CommandNote]`, `[CommandSideEffects]`, `[PipelineInput]`, `[CommandOutput]`). Will appear in next reference regeneration.
- **`debug`** — already well-annotated; was simply missing from the generated reference. Will appear on next regeneration.
- **`logout`** — alias of `exit` via heuristic alias map (same class, different constructor argument). Grouped automatically.
- **`usage`** — alias of `du` via heuristic alias map. Grouped automatically.
- **`sort-by`** — alias of `sort` via heuristic alias map. Grouped automatically.
- **`forget`** / **`unset`** — aliases of each other; already annotated.

All remaining gaps are alias-grouping issues resolved by the heuristic map or will appear after the next `dotnet run --project tools/SpecGenerator` invocation.

---

## Smaller items worth filing

These don't warrant full sections but should be tracked:

- **`fluid` struct semantics** are listed as a keyword (line 570) but the difference between mutable-field structs and immutable-field structs deserves an example in the "Struct Definitions" section (2480).
- **Doc-comment-to-help rendering pipeline** isn't documented — what `help <function>` actually shows from `@param`, `@example`, etc.
- **Implicit pipeline return** (functions emit the last pipeline's values when no `return`) is mentioned in the architecture doc but not formalized in the spec under "Functions".
- **`collect` aggregation** behavior — the streaming-vs-collection return distinction is mentioned in `ARCHITECTURE.md` but not in the spec.
- **Glob expansion behavior** — when does a bareword like `*.cs` glob, when doesn't it. Likely in `command-reference.tex` but worth verifying.
- **Pattern matching with type tests** — `case (int)` or `case (string)` if supported. Verify against `MatchArmSyntax`.
- **Recent diagnostic improvements:**
  - The "which `where` clause failed" marker ([ToshEngine.cs:7665](../src/Tosh.Language/ToshEngine.cs#L7665)).
  - The single-diagnostic-per-clause recovery in type-refinement blocks ([ToshParser.cs:4243-4272](../src/Tosh.Language/Parsing/ToshParser.cs#L4243-L4272)).
  - `split` preserving input type ([SplitCommand.cs:50-72](../src/Tosh.Core/Commands/SplitCommand.cs#L50-L72)) — affects how pipeline output types are described.
- **Generated vs. normative command reference.** Section II "Command Reference" and the former "Appendix I: Command Reference" both pulled from the same generated [`docs/spec/command-reference.tex`](spec/command-reference.tex) file via `\InputIfFileExists`. The duplicate appendix copy was removed 2026-05-06; Part II is now the single canonical surface. Future contributors editing `command-reference.tex` should remember it is regenerated from runtime metadata (`tosh --export-command-metadata`) — hand-edits will be lost on the next regeneration.

---

## Cheatsheet coverage status

Companion to the spec are seven two-page LaTeX cheatsheets in [`docs/cheatsheets/`](cheatsheets/): **basics**, **syntax**, **control-flow**, **collections**, **filesystem**, **units**, **interop**. They cover ~85% of the language well, but have gaps that mirror the spec gaps — refinements, subcommands, and concurrency are the biggest holes.

### Updates needed in existing cheatsheets

#### `syntax/syntax.tex`

**Operators table** at [syntax.tex:58-71](cheatsheets/syntax/syntax.tex#L58-L71) is incomplete. Missing rows:

- `is in` / `is not in` (membership; `is in` also does substring on strings — call this out)
- `contains`, `starts-with`, `ends-with` (string operators)
- `**` (exponent)
- `..` (range — has its own section but worth a row in the operators table)

Also: line 66 has `is-not` (with hyphen) — the actual operator form is `is not` (separate words). Fix typo.

#### `control-flow/control-flow.tex`

- **`throw` as expression form.** Currently only the statement form is shown. Add a one-line example: `cond ? value : throw "..."`.
- **Pattern Catalog** is good but should add the `case >= 500`-style comparison-pattern form for `switch/case` (currently only shown for `match`).

#### `interop/interop.tex`

**"Class Member Forms" section is the biggest gap in the existing cheatsheets.** Currently shows: stored prop, computed prop, get/set, static. Missing the entire modifier menu: `vital`, `lazy`, `fading`, `guarded`, `fixed`, `local`, `raw`, `hermit`, `hollow`, `partial`, `strict`. Same gap as [Gap §2](#gap-2--class-member-modifiers).

Recommended: add a "Modifier Reference" subsection with a table of the ~13 documented modifiers (one row each, semantics in 6-8 words). Skip `proud`/`shared` until you've code-traced them.

#### `basics/basics.tex`

**`$tosh.*` namespace** at [basics.tex:273-280](cheatsheets/basics/basics.tex#L273-L280) shows three example fields. [Gap §7](#gap-7--tosh-runtime-namespace) enumerated ~20 paths — the cheatsheet could expand to a small table covering the most useful ones (`$tosh.Last.{Result,ExitCode,Duration}`, `$tosh.Script.{Path,Args}`, `$tosh.Function.{Args,Input}`, `$tosh.Session.JobCount`, `$tosh.Host.{Version,RuntimeId}`, `$tosh.IsLoginShell`).

#### `collections/collections.tex`

**Lowercase type-alias note** — the runtime accepts `list<T>`, `dict<K,V>`, `set<T>`, `array`, `hashtable`, `table`, `tuple` as type annotations. The cheatsheet uses these implicitly but doesn't enumerate them. A small table mapping each shorthand to its CLR backing type would close the same loop as the spec-level type-alias gap.

### New cheatsheets to create

#### 🆕 `refinements/` — Refinement Types & Coerce ⭐⭐⭐

Largest documentation gap in both spec and cheatsheets. Closes ~30% of the user-visible documentation gap on its own.

**Page 1 — Definitions:**
- Type alias with `where`: `type X = base where _ ...`
- Block form: `type X = base { where ..., coerce ..., if ... coerce ... }`
- Refinement on parameters and `var`
- The `_` current-value reference
- Common one-liner refinements (`NonEmpty`, `Trimmed`, `AbsPath`, `Port`, `Percent`)

**Page 2 — Coerce mechanics:**
- The three forms (unconditional fallback, guarded eager `if X coerce Y`, inline `where ... coerce ...`)
- Lifecycle (guarded → predicates → fallback → re-evaluate)
- Recipe table: when to use each form (normalize / repair / clamp / canonicalize)
- Diagnostic shape: the `→ where: ... (failed)` marker
- Limitations note: no generic instantiation (`list<PathString>` not supported; use `each _ as PathString`)

#### 🆕 `scripts/` — Scripts & Subcommand Dispatch ⭐⭐⭐

Second largest gap. ToSh has a real script-as-CLI runtime that is undiscoverable today.

**Page 1 — Script structure:**
- Shebang and invocation forms
- `flag <name>: <type> = <default>` and `arg <name>: <type>`
- `subcommand <name> { ... }` blocks, nesting
- Arrow form: `subcommand greet(name) => ...`
- Modifier table: `eager`, `hidden`, `hollow`, `vital`

**Page 2 — Dispatch & recipes:**
- Resolution rules (leaf-to-root flag lookup, first-positional-as-child, `--` sentinel)
- Auto-`--help` and how to override it
- Real script template (echoing `build.tosh` shape — sectioned, with subcommands)
- Diagnostic codes for invocation errors
- "Promoting a script to a callable command" (the `~/.local/bin/path` pattern)

#### 🆕 `concurrency/` — Concurrency Primitives ⭐⭐

Large surface barely covered today. `basics` has 6 lines on jobs; nothing else exists.

**Page 1 — Process & job model:**
- `spawn` / `wait-for` / `Job` records
- Background pipelines (`cmd &`)
- `jobs`, `bg`, `fg`, `kill`
- `parallel`, `race`, `settle`

**Page 2 — Async & channels:**
- `async` / `await` on callables and blocks
- `channel`, `channel-send`, `channel-recv`, `channel-close`, `channel-select`
- Common patterns: producer/consumer, fan-out/fan-in, timeout via `race`
- `signal`, `timeout`

#### 🆕 `diagnostics/` — Errors & Debugging ⭐

Smaller but useful as a single landing page. Depends on having the diagnostic-code appendix in the spec first ([Gap §9](#gap-9--diagnostic-code-reference)).

**Page 1:**
- `try/catch/finally` with diagnostic codes (cross-link to control-flow)
- `inspect`, `members`, `describe-type`, `type-of`, `name-of`
- `debug` command and step-through
- The `tosh.*` diagnostic-code naming convention

**Page 2:**
- Diagnostic-codes reference table (most-likely-encountered codes)
- Custom diagnostics in user functions
- Common debugging recipes (introspecting pipeline values, finding why a refinement failed, tracing `$tosh.Last.*`)

### Lower priority / skip

- **Events/handlers** — `handles`, `when`, `priority` could fit as a section in `basics` or `interop` (a one-time event-handler workflow is rare enough that a full cheatsheet feels excessive).
- **TUI applications** — only relevant to advanced users building `tui pick`-style tools. The interop sheet could absorb a small section if anyone asks.
- **Doc-comment `@tags`** — fits as a 6-line block in the `syntax` sheet's "Functions" section, no need for its own.

---

## Recommended documentation work order

Combined order across spec and cheatsheets — both surfaces should advance together.

| Step | Surface | Item | Status | Effort |
|---|---|---|---|---|
| 1 | Spec | [Gap §1](#gap-1--refinement-types-and-coerce-mechanics) — Refinement types + coerce mechanics section | ✅ Done 2026-04-29 | High |
| 2 | Cheatsheet | New `refinements/` cheatsheet | Open | Medium |
| 3 | Spec | [Gap §8](#gap-8--subcommand-dispatch-system) — Subcommand dispatch chapter | ✅ Done 2026-04-29 | High |
| 4 | Cheatsheet | New `scripts/` cheatsheet | Open | Medium |
| 5 | Spec | [Gap §2](#gap-2--class-member-modifiers) — Class member modifier index macros | ✅ Done 2026-04-29 | Low |
| 6 | Cheatsheet | Update `interop/interop.tex` "Class Member Forms" with modifier table | Open | Low |
| 7 | Spec | [Gap §7](#gap-7--tosh-runtime-namespace) — `$tosh.*` namespace table | ✅ Done 2026-04-29 | Low |
| 8 | Cheatsheet | Expand `basics/basics.tex` `$tosh.*` snippet to small table | Open | Low |
| 9 | Cheatsheet | New `concurrency/` cheatsheet | Open | Medium |
| 10 | Spec | [Gap §9](#gap-9--diagnostic-code-reference) — Diagnostic-code appendix (build-generated) | ✅ Done 2026-04-29 | Medium-High |
| 11 | Cheatsheet | New `diagnostics/` cheatsheet (depends on #10) | Open | Low |
| 12 | Spec | [Gaps §3–§6](#gap-3--is-in-substring-behavior) — `is in` note, `throw` as expression, pattern forms, doc-comments | ✅ Done 2026-04-29 | Low |
| 13 | Spec | Type aliases table: `array`, `hashtable`, `table` rows + `\ikw{redirection}` | ✅ Done 2026-04-29 | Trivial |
| 14 | C# | `SourceCommand` metadata attributes | ✅ Done 2026-04-29 | Low |
| 15 | Spec | [Gap §10](#gap-10--compiler--profiles) — Compilation part (CLI, profiles, CLR shape, MSBuild, conformance, diagnostics) | 🟡 In flight 2026-05-06 | High |
| 16 | Cheatsheet | New `compiler/` cheatsheet (depends on #15) | Open | Medium |

---

## Language spec update workflow

Use this workflow whenever an implementation change affects ToastScript syntax,
semantics, diagnostics, builtin behavior, compilation profiles, or public CLR
shape. The goal is for the spec to be the language contract, not a delayed
description of whichever path the interpreter or compiler currently happens to
take.

1. **Classify the change before editing docs.**
   - Normative language behavior belongs in
     [`docs/spec/toastscript-spec.tex`](spec/toastscript-spec.tex).
   - Builtin command behavior belongs in command metadata attributes, then in
     generated [`docs/spec/command-reference.tex`](spec/command-reference.tex).
   - Diagnostic-code behavior belongs in source diagnostics, then in generated
     [`docs/spec/diagnostic-codes.tex`](spec/diagnostic-codes.tex).
   - Compiler/profile behavior belongs in the spec's Compilation part
     ([`docs/spec/toastscript-spec.tex`](spec/toastscript-spec.tex),
     `\part{Compilation}`); deeper internals stay in
     [`COMPILED_TOSH.md`](COMPILED_TOSH.md) and
     [`FIRST_CLASS_DOTNET_STATUS.md`](FIRST_CLASS_DOTNET_STATUS.md).
   - Implementation-only details belong in architecture notes, not the
     normative language spec, unless users can observe or depend on them.

2. **Audit the implementation surface.**
   - Parser/lexer: tokens, operators, soft keywords, statement forms,
     expression forms, and precedence.
   - Syntax model: AST nodes, doc-comment shapes, modifier flags, and source
     span behavior.
   - Binder/type checker/lowerer: name resolution, profile diagnostics,
     narrowing, refinement checks, and lowered forms.
   - Compiler emitter: native IL, runtime-hosted fallback, source replay,
     public CLR metadata, and refasm shape.
   - Runtime/stdlib: builtin metadata, pipeline contracts, side effects,
     diagnostics, and examples.
   - Tests: `CompilerFeatureMatrixTests`, evaluator/compiler parity tests,
     focused regression tests, and C# consumer/refasm tests.

3. **Update the normative spec in the same change as the implementation.**
   - Add grammar/syntax examples before prose if the feature introduces a new
     form.
   - Describe runtime semantics, compile-time semantics, and profile limits
     separately when they differ.
   - State interpreter/compiler parity expectations explicitly for features
     that cross both execution paths.
   - Document all user-visible diagnostics by code.
   - Mark intentional limitations as limitations, not vague TODOs.
   - Mark future/aspirational behavior as future behavior so it cannot be
     mistaken for a current promise.

4. **Regenerate generated spec artifacts instead of hand-editing them.**
   - Do not hand-edit `docs/spec/command-reference.tex`; regenerate it from
     command metadata:

     ```bash
     dotnet run --project src/Tosh.Cli/Tosh.Cli.csproj --no-build -- --export-command-metadata --latex -o docs/spec/command-reference.tex
     ```

   - Do not hand-edit `docs/spec/diagnostic-codes.tex`; regenerate it after
     adding or changing diagnostic codes:

     ```bash
     scripts/extract-diagnostic-codes.tosh
     ```

   - A normal `Tosh.Cli` build also runs the command-reference generator, the
     LaTeX spec build when `latexmk` is available, and the advisory parity
     check unless disabled:

     ```bash
     dotnet build src/Tosh.Cli/Tosh.Cli.csproj
     ```

5. **Run the existing parity machinery.**
   - The `ToshParityCheck` target is advisory and should surface warnings for:
     - missing command metadata;
     - parser hardcoded command names that do not resolve through the registry;
     - operator parity drift.
   - Treat parity warnings as documentation debt unless there is a deliberate
     reason to suppress or defer them.
   - If the full solution build is too expensive for a docs-only iteration, run
     the narrow `Tosh.Cli` build above and note any skipped validation.

6. **Keep status docs synchronized.**
   - Update [Spec coverage at a glance](#spec-coverage-at-a-glance) when a spec
     section changes.
   - If a gap is closed, mark it closed with the date and a short summary; move
     or delete old investigation text once it stops being useful.
   - If a new gap is found, add it under "Major gaps" with the same structure:
     status, what needs documenting, source files, recommended location, and
     tests/parity checks that should prove it.
   - When a cheatsheet is created or updated, mark the corresponding row in the
     [Recommended documentation work order](#recommended-documentation-work-order)
     table as done or replace it with the next useful item.
   - When the change affects compiled language status, update
     [COMPILED_TOSH.md](COMPILED_TOSH.md) and
     [FIRST_CLASS_DOTNET_STATUS.md](FIRST_CLASS_DOTNET_STATUS.md) in the same
     documentation pass.

7. **Use the feature matrix as the spec's executable checklist.**
   - Every syntax/operator/modifier/concept in the spec should eventually have
     a `CompilerFeatureMatrixTests` row or a linked runtime/spec test.
   - Every matrix row should point back to one of:
     - native IL / Tier 1;
     - runtime-hosted / Tier 2;
     - source replay / Tier 3;
     - deliberate unsupported diagnostic.
   - This gives the project one loop: spec promise -> implementation path ->
     tests -> generated docs -> parity check.

This document should age with the spec, compiler matrix, and cheatsheets. As
the spec catches up, this file should shrink toward a concise status ledger
rather than becoming a second language manual.
