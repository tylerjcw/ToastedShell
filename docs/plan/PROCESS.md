# How the work is run

Process notes carried over from the stabilization plan: why the programme was
sequenced as it was, how work is grouped into gates, what counts as done, and the
test strategy each item is held to.

The specification remains the user-facing language contract. When the plan changes
that contract deliberately, implementation, conformance tests, specification, help,
LSP and MCP metadata change together.

---

## Why Stabilization Comes First

ToastScript already has a broad and compelling surface: structured
pipelines, CLR interop, functions, classes, generics, records, modules,
concurrency, an interpreter, a compiler, and language services.

The principal risk is now duplicated semantics. Truthiness, operators,
assignment, callable binding, class construction, and language metadata
are each implemented in more than one place. Small syntax or execution
mode changes can therefore change program meaning.

Until the P0 and P1 work below is complete:

1. Safety and semantic convergence take priority over new syntax.
2. A fix is incomplete if it only repairs one execution surface.
3. New behavior requires an executable conformance example.
4. Optimized compiler paths may specialize only when they preserve the
   canonical runtime semantics.

## Work Streams

The `P0`–`P3` tables below are ordered by *severity*. This index is the
orthogonal view: which items belong to the same body of work, and which of them
gate self-hosting and a native target.

It is an index only — the tables remain the source of truth, and nothing here
changes their tooling. Item ids are deliberately not written in the `| \`id\` |`
row form, so `scripts/file-item.tosh` anchor matching stays unambiguous.

### Gate A — blocks self-hosting

Nothing compiler-shaped compiles until these land. Established by writing a
370-line lexer/parser/AST/visitor probe in ToastScript; see
`docs/SELF_HOSTING_RFC.md`.

| Stream | Items |
|---|---|
| **Type system — critical** | TS-P2-107 (subclass not assignable to base), TS-P2-108 (`match` arms do not narrow), TS-P2-109 (interpreted/compiled divergence) |
| **Type system — soundness** | TS-P2-87 (rebound variable keeps first inferred type), TS-P2-99 (interfaces unusable as annotations), TS-P2-85 (computed property on a struct), TS-P2-106 (class cannot name itself in its own return annotation), TS-P2-98 (unqualified refinement types) |
| **Dispatch & higher-order code** | TS-P2-93 (callable in a property), TS-P2-94 (`&` on a method), TS-P2-92 (`T.Prop.Method()`), TS-P2-103 (`shy shared func` unreachable from its own class), TS-P2-104 (splat arguments) |
| **Backend agreement** | TS-P1-13 (compiled vs interpreted assignment order), TS-P1-40 (two live index-assignment implementations), TS-P1-46 (array literal representation), TS-P1-47 (base-annotated variable rejects a subclass), TS-P1-48 (compiled assemblies share one global class registry), TS-P3-23 (differential execution across backends — **the corpus that found 46 and 47**) |
| **Scale ergonomics** | TS-P2-89 (top-level `defer`), TS-P2-91 (dotted module leaf not auto-partial), TS-P2-105 (`as` precedence), TS-P3-14 (bitwise operators), TS-P3-05 (thrown-value protocol), TS-P3-02 (`let` bindings), TS-P3-04 (stream/collection shape) |

### Gate B — blocks a native target

Filed 2026-08-13 after measuring tier coverage: **57 of 72 tracked features
already reach Tier 1 (pure IL)**; 13 are Tier 2 and 2 are Tier 3.

| Stream | Items |
|---|---|
| **Subset definition** | TS-P3-15 (define the `no_clr` subset), TS-P3-16 (ToastScript-owned core types + conformance corpus) |
| **Tier promotion** | TS-P3-17 (builtin command dispatch — the stdlib port, largest single item), TS-P3-18 (defaulted parameters, the only Tier-3 entries), TS-P3-19 (annotated/fixed/refinement variable writes) |
| **Native runtime** | TS-P3-20 (regex engine or documented dialect), TS-P3-21 (GC, object layout, startup budget) |
| **Backend** | TS-P3-22 (emit C) |
| **Native FFI (C ABI is a peer FFI in the target design)** | TS-P2-90 (native export tables shared per library path), TS-P2-88 (`-> ok` yields its return value) |

### Gate C — neither; deferrable

| Stream | Items |
|---|---|
| **CLR interop (.NET target only)** | TS-P2-95 (InnerException dropped), TS-P2-96 (`load-assembly` forces the type closure), TS-P2-97 (generic static overload resolution), TS-P2-100 (a `System` module shadows the namespace) |
| **Diagnostics & docs** | TS-P2-101 (class doc comments never reach `help`), TS-P2-26 (spec examples never executed), TS-P2-09 (LSP maps warnings to errors) |
| **Shell, tooling, UX** | TS-P2-86, TS-P3-03, TS-P3-06, TS-P3-07, TS-P3-08, TS-P3-09, TS-P3-12 |
| **Development infrastructure** | TS-P2-38 (suite memory exhaustion — blocks nothing, hurts daily) |

## Definition of Done

An item is closed only when all applicable conditions hold:

- the smallest public reproduction has a regression test;
- interpreter and compiled behavior agree, or an unsupported compiler
  shape produces a deliberate structured diagnostic;
- cancellation and streaming behavior are tested where relevant;
- diagnostics use a stable `tosh.*` code rather than leaking raw CLR
  exceptions;
- the specification and generated/user-facing metadata agree;
- focused tests and the full solution test suite pass;
- this document's status and evidence are updated; and
- the work is committed. Validated work that exists only in the working
  tree is one command from loss, which no amount of test coverage
  protects against.

## Test Strategy

### 1. Specification conformance corpus

Representative examples in the specification should exist as executable
fixtures with expected typed results and diagnostics. The corpus should
include successful and failing programs.

Priority evidence (July 25 review): four specification examples currently
fail as written — `var small = 10kb` (`TS-P2-14`), `(10kb > 5kb)`
(`TS-P2-14`), `where _.Name =~ "\.cs$"` (`TS-P2-12`), and
`$myDict | entries` (the `entries` command does not exist). Extracting the
spec's examples into fixtures would have caught all four mechanically.

### 2. Differential execution

Run the same corpus through:

- the ordinary interpreter;
- any bound/optimized interpreter path;
- compiled assemblies under `Tosh.Compiler.Runtime`; and
- MCP execution where cancellation or diagnostic transport matters.

Compare values, CLR value types, stdout, stderr, diagnostic codes, and
exit behavior. Operator tests that only prove a switch case is reachable
are not semantic parity tests.

### 3. Invariant-focused tests

Assignments and construction need explicit tests for:

- evaluation count and order;
- resolve-before-mutate behavior;
- partial failure;
- annotations and generic type parameters;
- const/immutable bindings;
- nearest lexical scope; and
- interpreter/compiler agreement.

### 4. Generated-surface validation

Every parser-recognized keyword, operator, and declaration kind should
be checked against completion, hover, semantic tokens, document symbols,
help, MCP metadata, and specification generation.

### 5. Lexer/parser fuzzing

Random and grammar-aware inputs through the lexer, parser, and binder
must never produce a raw CLR exception or terminate the process. The
`tosh check` mode (`TS-P3-01`) is the natural harness. Seed classes from
the July 25 review: dotted-number barewords (`TS-P2-13`), oversized
binary/octal literals (`TS-P2-05`), deep expression nesting and recursion
(`TS-P0-07`), and malformed range forms.

