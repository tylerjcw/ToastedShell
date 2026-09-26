# TōSh / Tōast — improving the proof of concept

> **Status:** active — written 2026-09-25.
> **Items:** [`TOSH-0012`](plan/items/TOSH-0012.md) (P0.1, complete),
> [`TOSH-0013`](plan/items/TOSH-0013.md) (P0.2) and [`TOSH-0014`](plan/items/TOSH-0014.md)
> (found during P0.1) are filed; the rest of this plan is listed here until it is started.

This .NET implementation is the proof of concept for Tōast. The final language is a separate,
greenfield Rust implementation with its own runtime: a lossless syntax tree, a semantic model,
typed HIR, a target-neutral MIR, compiled backends and a bytecode VM for the interpreter and the
shell, gradual typing, ownership and borrowing with an optional ARC-based `gc` tier, `Result` with
`?` instead of exceptions, and structured concurrency. Its design lives in that repository
(architecture overview; Proposal 001, accepted 2026-09-19; Proposal 002, draft).

That settles what this codebase is for, and so what is worth changing in it:

1. **It is a shell someone lives in.** Anything that loses data or does something destructive the
   user did not ask for is fixed first, whatever the Rust plans.
2. **It is where the Rust design's open questions can be answered with evidence** rather than
   argued about — shell-mode name resolution, stream versus collection semantics, what breaks when
   scoping becomes lexical.
3. **Its knowledge should outlive it.** Eight thousand tests, a diagnostic catalogue, a command
   inventory and a Tōn conformance suite are worth carrying into the Rust repository.

It is *not* worth re-architecting. The evaluator rewrite planned as Phase B of
[TOAST_SEPARATION_PLAN.md](TOAST_SEPARATION_PLAN.md) and §4 of
[COMPILER_DECOMMISSIONING_PLAN.md](COMPILER_DECOMMISSIONING_PLAN.md) — a bound-tree evaluator,
slot-indexed frames, a bytecode VM — is what the Rust implementation is. Doing it twice would
spend the effort on the implementation that is going away.

---

## What was measured

Every row below was reproduced on 2026-09-25 against the Debug build of `claude/wizardly-mayer-tot5ag`,
most of them in a scratch directory. The repository's companion memories hold the same findings
(`scripts/devcompanion.sh recall "review-2026-09-25"`).

Rows marked *fixed* keep what was measured before the fix, so the table stays a record of what
the proof of concept did.

| # | Behaviour | Reproduction | What happened | Plan |
|---|---|---|---|---|
| 1 | A value becomes a flag | `var name = "-r"; rm $name victim` | `victim/` was deleted recursively; the file named `-r` was left alone | P0.2 |
| 2 | External output is not binary-safe | `/usr/bin/cat bin.dat \| /usr/bin/sha256sum` | 100,000 random bytes arrived as 181,127 (invalid UTF-8 replaced by U+FFFD) | P0.1, fixed |
| 3 | Redirection is not binary-safe | `/usr/bin/cat bin.dat out> out.bin` | the same 181,127 corrupted bytes; `… \| /usr/bin/gzip -c out> f.gz` wrote an archive that decompresses to nothing | P0.1, fixed |
| 4 | A newline is added | `/usr/bin/cat nonl.txt \| /usr/bin/wc -c` | 19 bytes became 20 | P0.1, fixed |
| 5 | Pipes between programs are slow | `/usr/bin/cat big.txt \| /usr/bin/wc -l`, 50 MB | 3.4 s of piping, against 44 ms in bash | P0.1, fixed |
| 6 | A captured pipeline reads the terminal | `var x = (/usr/bin/printf "hello\n" \| /usr/bin/tr a-z A-Z)` at a TTY | `tr` read what was typed at the keyboard, not `printf`'s output | P0.1, fixed |
| 7 | `echo` output is held back | `for i in 1..3 { sleep 1; echo $i }` | nothing for 3.5 s, then one table; a script's whole output is merged into one table at exit | P1 |
| 8 | Piped output is table art | `tosh -c 'echo hello' \| cat` | `┌────────┬───────┐ │ String │ hello │`, plus ANSI colour in diagnostics | P1 |
| 9 | `parallel` drops writes | `var total = 0; 1..200 \| parallel { $total = $total + 1 }` | `$total` is still 0, with no diagnostic | P1 |
| 10 | Stopping early over-runs | `1..5 \| each { $n = $n + 1; $_ } \| first 1` | the `each` body ran twice | P1 |
| 11 | Results collapse by count | `var r = [1, 2] \| where { … }` | 0, 1 or n survivors give `null`, a bare `Int32` or an array; `[7] \| where { $_ > 0 }` turns an array into an int | P2 |
| 12 | Calls are dynamically scoped | a function assigning `$shared` | it overwrote its caller's `var shared`; a consumer block reads a suspended generator's locals; the specification says lexical | P2 |
| 13 | One parse error per file | three independent errors | the first, plus a cascade from it | P2 |
| 14 | Process group set after start | `src/Tosh.Stdlib/ExternalProcessCommand.cs:257` | `setpgid` runs once the child is already executing — the race classic shells avoid by setting it before `exec` | later |

---

## P0 — data safety

### P0.1 Binary-safe external I/O — [`TOSH-0012`](plan/items/TOSH-0012.md)

**Problem.** Values move between pipeline stages as objects, and an external program's stdout
becomes one `ShellTextLine` per line. That is right when a TōSh stage consumes the output and wrong
when the consumer is another program or a file. `ExecuteWithPipesAsync` decodes stdout as UTF-8
and splits it into lines (`ConsumePlainStdoutAsync`); the next program's stdin is fed
`WriteLineAsync(ExternalTextSerializer.Serialize(item))` (`PumpStandardInputAsync`); `out>` writes
every value the pipeline yields as a text line
(`EvaluatePipelineWithRedirectionAsync`); and `in<` feeds the first stage the file's lines
(`ReadLinesAsync`). Rows 2–5 are the result.

Row 6 is a separate defect in the same code. `var x = (a | b)` passes `outputIsCaptured` to every
stage, and at a terminal `DetermineSpawnMode` answers `Hybrid` for all of them — stdout piped,
stdin and stderr inherited. That is what the *first* stage of a captured pipeline wants, so an
interactive child can still prompt; for every later stage it throws away the pipeline's data and
reads the keyboard instead.

**Design.** A byte hand-off between adjacent stages that both deal in bytes:

- `RawByteHandoff` (`Toast.Runtime`) is created by the engine between each pair of adjacent
  command stages, and between the last stage and a single `out>` file. The engine binds each side
  to the `CommandContext` it built for that stage, so a command a stage invokes internally (a
  callable run by `each`, a renderer) can never claim it — `CommandContext` is a record, and
  `context with { … }` copies carry the reference along, but a copy is a different object.
  (The first draft bound it to the resolved command instead; a registered command is one object
  shared by every stage that names it, so the context is the precise key.)
- The consuming side offers a destination before it starts pulling: an external command in piped
  mode offers its stdin; the engine offers the redirection's file.
- The producing side, an external command whose stdout turned out to be plain rather than TSSP,
  claims the destination and copies bytes into it — the sniffed prefix first — and yields no
  items. If nothing was offered, it yields lines exactly as before.
- A consumer that exits early (`… | head -c 10`) makes the copy fail with a broken pipe; the
  producer then closes its read end, so the child meets a closed pipe instead of blocking on one
  nobody drains. It then reports `EPIPE` rather than dying of `SIGPIPE`, because programs started
  by TōSh inherit `SIGPIPE` as ignored — [`TOSH-0014`](plan/items/TOSH-0014.md).
- `in<` works the same way in reverse: when the first stage offers its stdin, the file's bytes are
  copied into it.
- Only the first stage of a captured pipeline at a terminal runs in hybrid mode. Later stages have
  a pipeline behind them (`CommandContext.HasUpstream`) and run piped.

**Not changed.** TōSh stages still see text lines, and TōSh values still reach a program's stdin
serialised one per line. A producer speaking TSSP still yields records. stderr is still relayed
as text. Builtins such as `cat` stay text-oriented for now; they can adopt the same hand-off later
(`cat file | gzip` is the common bash reflex, and it runs the builtin).

**Acceptance.** `cat | sha256sum`, `cat out> file`, `cat | gzip -c out> f.gz`, `gzip -dc in< f.gz`
and a file without a trailing newline all preserve every byte; 50 MB through `cat | wc -l` runs
at pipe speed; the captured-pipeline case returns `HELLO`; the existing external-process tests
still pass.

**Done, 2026-09-26.** Every case above holds, and 50 MB now costs about 0.1–0.2 s over the
shell's own start-up instead of 3.4 s. Three more defects in the same code were fixed with it:

- *Background pipelines* (`… &`) had their own copy of the plumbing. `yes | head -1 &` stayed
  "running" forever, because the pump that gave up when `head` left kept `yes`'s stdout open; every
  `out>` file began with a UTF-8 byte-order mark; and `out>` and `in<` decoded bytes as lines.
- *The protocol sniff* waited for eleven bytes before deciding a program's output was not TSSP,
  so `printf ab; sleep 3` reached the next program after three seconds. It now decides at the first
  byte that differs.

Measurements, tests and known limits are in [`TOSH-0012`](plan/items/TOSH-0012.md).

### P0.1 follow-up — `SIGPIPE` — [`TOSH-0014`](plan/items/TOSH-0014.md)

Found while testing P0.1: .NET ignores `SIGPIPE`, an ignored signal survives `exec`, and so every
program TōSh starts has it ignored (`SigIgn: …1000`, against `…0000` under bash). A writer whose
reader has gone gets `EPIPE` and usually says so — `yes: standard output: Broken pipe` — where
under bash it ends silently; a background job then reports `failed`. `PosixSignalRegistration`
does not change it. The options are a launcher that resets it (`env --default-signal=PIPE` on GNU
systems, or `posix_spawn` with `POSIX_SPAWN_SETSIGDEF`) or documenting it. Priority 1: it is noise
and wrong status, not data loss.

### P0.2 Values never become flags — [`TOSH-0013`](plan/items/TOSH-0013.md)

**Problem.** Builtins receive `CommandContext.Arguments` as evaluated `object?` values and find
their options by comparing `ToString()` against option spellings (`HeadCommand.ParseArguments` is
typical). By then the engine has thrown away whether an argument was written as a literal `-r` or
produced by a variable, a subexpression, an interpolation or a glob. Row 1 is the consequence, and
it applies to any value that starts with a dash: a filename from `ls`, user input, a loop variable.
55 files in the standard library match flags by string.

**Design.** The engine records the provenance of every argument it passes — whether it came from a
literal, unquoted word in the source — and `CommandContext` exposes it. A command treats an
argument as an option only if it is literal:

- `rm -r dir` still works; `rm $name` with `$name = "-r"` removes a file called `-r`.
- A quoted literal (`rm "-r"`) is a value, which is also how to name such a file on purpose.
- `--` ends option parsing.
- Glob results are values: a file called `-rf` matched by `*` cannot become an option.

Destructive commands go first — `rm`, `mv`, `cp`, `chmod`, `chown`, `kill` — then the other 49 files.
External programs are out of scope: their argv is theirs to interpret, exactly as in bash. A lint
suggesting `--` when a variable starting with `-` reaches a program is a P2 candidate.

**Acceptance.** For each of the six commands, a flag spelled literally still works and the same
text arriving through a variable, a subexpression or a glob is treated as an operand.

---

## P1 — daily-use correctness

3. **Stream `echo` output.** `writeline` already streams raw text as it is written, which is the
   Rust design's `write`/`writeline`; `echo` is its `yield`, and the display should show yielded
   values as they arrive.
   - Stop collecting each top-level statement's values before yielding them
     (`src/Tosh.Language/ToshEngine.Statements.cs:223`). That collection is also what defeats
     `AutoDisplaySink`'s 250 ms streaming promotion in the REPL.
   - Run `-c` and script files through the streaming sink rather than `BufferingDisplaySink`
     (`src/Tosh.Cli/Program.cs:377`, `:388`), and never merge separate statements into one table.
   - When stdout is not a terminal, write plain values, one per line, with no box drawing, and no
     ANSI colour in diagnostics.
   - Print a lone scalar as itself rather than as a `Type | value` table.
4. **Commands that must stop reading.**
   - `head` and `tail` collect their whole input
     (`src/Toast.Stdlib/Text/HeadCommand.cs:59`, `TailCommand.cs:115`), so `yes | head -n 1` never
     returns; `head` should stop after N items and `tail` keep a ring buffer.
   - Children should be reaped when a pipeline stops early.
   - History should be appended atomically with mode 0600 (`src/Tosh.Runtime/HistoryFileStore.cs`),
     so concurrent shells stop overwriting each other.
5. **Make silent failures loud.**
   - Assigning an outer variable inside `parallel`, `race` or `async` should be an error rather
     than a write into a discarded copy of the scope.
   - Remove the one-item read-ahead that runs `each`'s body twice for `first 1` (the "lone
     collection" lookahead of `TS-P1-08` is the place to start).

## P2 — the proving ground for the Rust specification

The Rust specification still has to write down shell-mode name resolution, the stream model and
the migration path. This implementation can answer those with evidence.

6. **Shell-mode resolution rules.** Write down what the PoC does — scoped functions, then
   builtins, then module-qualified names, then a PATH lookup (where a TōSh script found on the
   path runs in-process), then auto-cd — together with the binder's typo rule, as the draft.
   Try `>`, `>>` and `2>` as redirections in command position, as the Rust design specifies; today
   `cat f > g` passes `>` and `g` to `cat` as arguments.
7. **A `strict` switch for final-design semantics** where they are cheap to try:
   - warn when a variable resolves through a caller's frame (row 12);
   - never collapse a pipeline's result by count (row 11, and the open research item
     [`TS-P3-04`](plan/items/TS-P3-04.md));
   - exit status as a result: `git status?` fails the enclosing script when `git` does.

   Running the author's own `config.tosh`, `profile.tosh` and scripts under it measures what the
   final semantics cost before they are frozen.

## P3 — carry the knowledge to the Rust repository

8. **A language-neutral conformance corpus.** Convert the C# tests' source-to-result cases into
   `.toast` files with expected output, tagged *same in the final design*, *changes*, or
   *CLR-specific*, with the `TS-`/`TOAST-` item that motivated each one. The Rust `tests/` tree is
   meant to verify semantics across every mode; this seeds it.
9. **Inventories.** The metadata of the ~209 builtins (`tosh --export-command-metadata`) for
   planning the standard library surface; the diagnostic catalogue; the Tōn conformance suite
   (`docs/spec/ton-conformance`); and the allocation-probe baselines to compare the Rust VM
   against.

## Stopped in this codebase

- The bound-tree evaluator, slot-indexed frames and the bytecode VM.
- Type-representation refactors (annotations are still strings re-parsed on every conversion).
- SIMD numeric kernels, fixed-order vector reductions and evaluator micro-optimisation.

Performance work continues only where daily use needs it — external pipes in P0.1.

---

## Lessons for the Rust specification

- **Streams are not lists.** The PoC's most persistent semantic trouble is deciding whether a
  value is a stream or a collection: collapse by count, "lone collection" read-ahead that repeats
  side effects, marker types patched on stage by stage. Specify a stream type distinct from
  `List<T>`, with explicit collection and no read-ahead.
- **Bytes stay bytes.** External stdin and stdout are byte streams, OS-piped between programs and
  decoded only at a Tōast stage. Specify how `>` is a redirection in command position and a
  comparison in expressions; the PoC side-stepped it with `out>`.
- **Flags come from syntax.** Builtins and subcommand `arg`/`flag` declarations bind by syntax;
  for external argv, lint values from variables that start with `-` and suggest `--`.
- **The host streams what `echo` yields.** Proposal 001 has a compiled entry point that "collects
  these pipeline objects" before serialising them; it should stream them, with a plain-text format
  when stdout is not a terminal.
- **The `gc` tier needs a mutation model.** `Rc<RefCell<T>>` turns "alias and mutate freely" into
  runtime borrow panics. Decide how shared values are mutated, how cycles are collected (events
  and closures that capture `$this` form cycles in exactly the scripting code the tier is for),
  and what happens when a `gc` value crosses `spawn` — the PoC's `parallel` silently dropped writes.
- **`%name%` needs a boundary.** Resolving names at run time across frames recreates the PoC's
  dynamic scoping; restrict it to module globals or an explicit table.
- **Consistency.** Proposal 001 §8 says "three tiers" and lists four, and its summary table still
  says "no GC"; "three ownership tiers plus a `gc` mode" resolves both.

---

## Progress

| Date | What |
|---|---|
| 2026-09-25 | Plan written; `TOSH-0012` and `TOSH-0013` filed; P0.1 started. |
| 2026-09-26 | P0.1 done: `TOSH-0012` complete, including the background-job path and the protocol sniff; `TOSH-0014` filed. |
