# Phase A — what it touches

*Survey, 2026-08-17. Measured, not estimated. Nothing edited yet.*

Phase A of `SELF_HOSTING_RFC.md` is **"Specify portable semantics"**. Its task list names
ten concerns:

> equality, hashing, ordering, nullability, overflow, Unicode, **formatting**, collection
> shape, streaming, and exception semantics

plus a core manifest and intrinsic boundary, and a conformance corpus **independent of BCL
names and behaviour**. Exit is: *core behaviour is specified in Tōast terms and enforced by
a backend-neutral corpus.*

Two items are filed against it — `TOAST-0014` (formatting) and `TOAST-0015` (streaming).
**That is two of ten.** The survey below is what each concern actually looks like in the
tree today.

---

## 1. The defect is real and demonstrable

Before anything else, the premise of `TOAST-0014` was checked rather than assumed:

```tosh
var d = new DateTime(2026, 8, 17, 12, 0, 0)
echo $"default:  {$d}"                              # 2026-08-17 08:00:00
$tosh.Config.Display.DateTime.ScalarMode = "Unix"
echo $"as unix:  {$d}"                              # 1786982400
$tosh.Config.Display.DateTime.ScalarMode = "Local"
echo $"as local: {$d}"                              # 2026-08-17 08:00:00
```

**The string a program builds changes when the shell's display settings change, at
runtime, mid-script.** This is the acceptance test the item asks for, and it already
passes as a demonstration of the bug.

There is a second, quieter half. The compiled path formats through
`ToshValueFormatter` (`src/Tosh.Runtime/ToshValueFormatter.cs`, 13 lines) — a static
wrapper over `new ObjectFormatter()`, which builds its registry from a **fresh**
`DisplayPreferences` rather than the shell's live one. So interpreted and compiled
interpolation agree only while the user has changed nothing. Nothing tests that they
agree.

---

## 2. Formatting — `TOAST-0014`

### Where it lives

| File | Lines | Role |
|---|---|---|
| `src/Tosh.Runtime/ObjectFormatter.cs` | 584 | the type-dispatch chain |
| `src/Tosh.Runtime/ToshValueFormatter.cs` | 13 | the compiler's entry point |
| `src/Tosh.Stdlib/BuiltInDisplayProfiles.cs` | — | registers ~25 profiles, several built from `DisplayPreferences` |
| `src/Tosh.Runtime/DisplayProfileRegistry.cs` | — | `CreateDefault` calls `EnsureStdlibLoaded` and invokes a registrar hook |

The four language call sites the item records are **confirmed exactly**:

| Site | What it is |
|---|---|
| `ToshEngine.cs:4181` | string interpolation |
| `ToshEngine.cs:3238` | value-to-text conversion |
| `ToshEngine.Diagnostics.cs:576` | a thrown value's text in a diagnostic |
| `ToshEngine.Pipelines.cs:270` | text written to a redirect target |

All four reach it as `Runtime.Formatter`, and `Formatter` is **not** on `ToastRuntime` —
it is a `ToshRuntime` member. So every one of these is a language→shell reference, which
is why `TOAST-0006` cannot finish around them.

### Correction to the item

`TOAST-0014` says the formatter "consults profiles in five places." Inside
`ObjectFormatter` it consults them at **one** site — `TryRenderProfile`, line 239, called
once from `FormatValue` line 52, as the *first* step before any type dispatch. The other
four callers of `TryRenderProfile` are outside the formatter (`DisplayEngine` ×3,
`Tosh.Cli` ×2), which is probably what was counted.

**This makes the work cheaper than filed.** Profile dependence is a single early-exit at
the top of the chain, not a concern woven through it. Everything below line 52 is
deterministic type dispatch:

```
null → profiles → simple scalars → shell value types → record fields →
Type → Exception → IDictionary → IEnumerable → object
```

### What is actually entangled

The chain interleaves portable value kinds with **shell-specific types**:
`ShellCommandDescriptor`, `FormatterStatus`, `CommandHistoryEntry`, `ObjectInspection`,
`ObjectInspectionMember`, `FileSystemEntry`, `FileSystemInfo`. Those are the seven cases a
split has to relocate, and they are the reason the file cannot simply move.

`TryFormatSimple` (line 174) is the closest thing to a portable core that exists:
`string`, `char`, `bool`, `Enum`, `DateTime`, `DateTimeOffset`, `Uri`, and `IFormattable`
restricted to primitives, `decimal`, `Guid`, `TimeSpan`, `BigInteger`, `ToshVector`,
`ToshMatrix`, `Quantity`. It is already `internal static` and takes no profile.

### Existing coverage to preserve

160 test cases across eleven files touch this: `ObjectFormatterTests` (23),
`FormatterTests` (24), `DisplayEngineTests` (61), `InterpolationFormatTests` (8),
`InterpolationHoleCallTests` (7), `InterpolationHoleReuseTests` (9),
`FormatRoundTripTests` (6), `DisplayProfileTests` (9), and three smaller. **These pin
today's behaviour including the profile dependence**, so the split has to decide which of
them are asserting language semantics and which are asserting display.

---

## 3. Streaming — `TOAST-0015`

### The rebinding

Eleven references, measured:

| File | Lines |
|---|---|
| `ToshEngine.Pipelines.cs` | 247, 248, 253, 254, 273, 274, 287, 292 |
| `ToshEngine.cs` | 1034, 1758 |
| `ToshEngine.Subcommands.cs` | 725 |

Four of those are the assignment pattern — save `Runtime.Output`, swap in
`CreateCompositeWriter(targets)`, restore in a `finally`. `Output` and `Error` are
`TextWriter` on `ToshRuntime` (lines 61, 63).

### Correction to the item

The item says this "wants the stream-handle concept to exist first, which is why it is
filed rather than done," and calls `ManagedFileHandle` "the nearest thing today."

`ManagedFileHandle` is 473 lines and already has: text and binary modes, read and write,
append, encodings, `CanRead`/`CanWrite`/`CanSeek`, `Position`, `Length`, `Seek`,
`CopyTo`, `Flush`, `Close`, `Dispose`, `DisposeAsync`, an id, and a global open-handle
registry. `Tosh.Stdlib/Filesystem/` already ships `OpenFileCommand`, `CloseCommand`,
`FlushCommand`, `PositionCommand`, `ReadFileCommand`, `ReadBytesCommand`,
`AppendFileCommand`, `CopyToCommand`, `ReadFromCommand`.

**The concept exists.** What does not exist is the *unification*: redirection targets a
`TextWriter` and the file commands target a `ManagedFileHandle`, and the session's own
writer is expressible as neither. That is a narrower and better-defined job than
"invent a stream abstraction," and it can start now.

---

## 4. The eight concerns with no item

| Concern | Where it lives today | State |
|---|---|---|
| equality | `OperatorEvaluator.AreEqual` (`:321`) | implemented, unspecified |
| ordering | `OperatorEvaluator.EvaluateOrderedComparison` (`:474`), `TryCompareByName` (`:562`), **and a second path** at `ToshEngine.cs:2609 CompareCore` | two implementations |
| hashing | no central site found | **absent** |
| nullability | scattered; `ToshTruthiness` (85 lines) covers truthiness only | partial |
| overflow | no `checked` policy found in `OperatorEvaluator` | **unspecified** |
| Unicode | inherited from `System.String` wholesale | **unspecified** |
| collection shape | `TS-P3-04`, status *research*, one-line acceptance | filed, not designed |
| exception semantics | `ToshError.cs` (73 lines), `.NET` exceptions underneath | partial |

Supporting mass: `TypeConversion.cs` is 748 lines and `OperatorEvaluator.cs` is 1,679 —
those two plus `ObjectFormatter` are where most portable semantics actually are.

`TS-P3-16` is the RFC's named plan item for this ("ToastScript-owned core types and their
conformance corpus"), and it is **one paragraph**, status *proposed*. It names `str`,
`int`, `dec`, `list`, `dict`, `date`, `duration`, `regex`. It is the item that should
carry most of this table, and it is not written yet.

---

## 5. The corpus vehicle already exists

Phase A exits on "a backend-neutral corpus." There is one to build on:

- `DifferentialExecutionTests.cs` (254 lines) runs the same program interpreted **and**
  compiled and asserts they agree. Its docstring is explicit that it exists because
  `TS-P2-109` returned 42 interpreted and 0 compiled and survived indefinitely because
  nothing compared the two. Its corpus is concentrated on class hierarchies.
- `SpecConformanceTests` (17 cases), `EqualityParityTests` (4),
  `CompilerOperatorParityTests` (14), `OperatorParityTests`, `BoundEvaluatorParityTests`.

So the harness pattern is proven. What is missing is breadth and the *specification* the
corpus would be checking against — today these tests assert that two implementations
agree, not that either matches a written contract.

---

## 6. What this suggests

**The order is not `TOAST-0014` then `TOAST-0015`.** Both are downstream of a decision
neither one states: *what is the portable value-to-text contract, and where does it live?*

A reading of the dependency structure:

1. **Write the contract first.** A `docs/spec/` section covering scalars, collections,
   records, `null`, NaN, signed zero, and Unicode — the list `TOAST-0014`'s fourth
   acceptance box already names. This is prose, not code, and it is what makes the rest
   mechanical.
2. **Extract the portable core.** `TryFormatSimple` plus the container walkers, with the
   seven shell value types relocated. `ObjectFormatter` keeps profiles and calls the core
   underneath, so the two cannot drift.
3. **Point all four language sites at it**, which is what frees `Output`/`Error` and
   unblocks `TOAST-0006`.
4. **Unify the stream handle** — `TOAST-0015` against `ManagedFileHandle`, not against a
   new abstraction.
5. **Then `TS-P3-16`**, rewritten to carry the eight unfiled concerns, with the corpus
   extended on `DifferentialExecutionTests`' pattern.

Steps 1–3 are `TOAST-0014` as filed, in the order that keeps the semantics decision out of
the mechanical diff — the discipline the whole separation has run on.

---

## 7. Open questions before any edit

1. **Does `$"{x}"` render a `DateTime` as ISO, or as the local-time form it produces
   today?** Today's default is `Local` via profile; the compiled path gives `"O"`. Whatever
   is chosen becomes a language guarantee, and one of the two current behaviours has to
   change.

2. **Does the portable core live in `Tosh.Runtime` or move to `Tosh.Language`?**
   `TOAST-0006` divides `Tosh.Runtime` anyway. Deciding this now avoids moving the file
   twice.

3. **Do the seven shell value types keep bespoke formatting, or become display profiles?**
   They are already special-cased *above* the generic object path, so turning them into
   profiles would be the uniform answer — but it changes `help`, `ls` and history output.

4. **Is Phase A scoped to formatting and streaming for now, with `TS-P3-16` rewritten to
   carry the rest as a separate arc?** The RFC's ten concerns are more than one work item,
   and only two are filed.

5. **`ToshEngine.Pipelines.cs:270`** — the item already flags this as possibly a different
   question: it formats a value on its way to a stream, which is serialisation rather than
   interpolation. If those are separate contracts, that is a fifth call site that does
   *not* move with the other three.
