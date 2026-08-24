---
id: TOAST-0035
title: "Source replay and implicit dynamic are how the compiler handles what it cannot emit"
status: partial
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Phase B's second bullet: "remove compiler-subset source replay and implicit dynamic
fallbacks". Both mechanisms are live.

**Source replay** — when the emitter cannot produce IL for a construct, the construct is
left to be re-executed by the tree-walking evaluator at runtime. `TOAST-0030` showed what
that costs when the fallback itself does not work: `class E extends Error { }` was handed
to replay and failed at runtime with "Command 'class' was not found", so a declaration that
runs interpreted did not run at all compiled. It is referenced in **61 places** across
`src/Tosh.Compiler`, `src/Tosh.Compiler.Runtime` and `src/Tosh.Language`, under the name
"Tier 3".

**Implicit dynamic** — `--compile-allow-dynamic` exists so a program with un-inferrable
locals can compile anyway, by falling back to dynamic dispatch.

## Measured 2026-08-21

- Eleven distinct "unsupported *X*" emitter diagnostics: assignment operator, block body
  stage, destructuring pattern, expression, first pipeline stage, interpolated part,
  literal type, member assignment target, numeric op, pipeline stage, statement.
- Constructs explicitly documented as staying Tier 3 include native `require` blocks and
  bind blocks.
- The readiness probe (`TOAST-0038`) hits `implicit_dynamic` four times.

## Why the order matters

Removing the fallbacks *first* would simply make working programs stop compiling.
`TOAST-0034` is the prerequisite for the dynamic half — with inference propagating,
most `implicit_dynamic` sites disappear rather than needing annotation — and the emitter
gaps behind the eleven "unsupported" messages are the prerequisite for the replay half.

The honest sequence is: make the fallback unnecessary, then remove it, then keep it removed
with a strict-mode gate.

## Measured against a real library — 2026-08-22

The `runtime` profile already refuses source replay, so it is the instrument for finding out
what is still replayed. Pointed at this machine's `ToastLib` — sixteen files, 2,559 lines,
the thing a user would actually want compiled:

**Fifteen of sixteen were rejected, every one for the same reason: `module body`.** Only
`Math.tosh` emitted.

That reduces to a short list, now pinned as `SourceReplaySurfaceTests` — what emits is a
corpus, what does not is a tripwire asserted to *still* fall back, so a fix cannot land
silently:

| Emits inside a module | Still replays |
|---|---|
| `func`, `var`, `class`, `class extends`, nested `module`, pipeline bodies, interpolation | `record`, `enum`, `interface`, `trait`, `union`, `struct`, refinement `type` |

### The blocker is not a declaration kind

Five of the sixteen files — `Bluetooth`, `Git`, `Native`, `Shell`, `System` — contain **none**
of the blocking declarations and are replayed anyway. `CanEmitClrModuleMethod` refuses any
parameter that is optional, rest, or defaulted, and a library is full of
`func FromGl(r: double, g: double, b: double, a: double = 1.0)`.

**A default parameter is therefore the single highest-value gap**, ahead of every declaration
kind. Usage in the measured library: `type` 18, `enum` 11, `interface` 2, `record` 1, and
`trait` / `union` / `struct` zero.

### Both halves already exist one level up

Neither gap needs new machinery, which is what makes this tractable:

- **Declaration kinds.** Every one of them already has a CLR shell emitted when it appears at
  the **top level** — `DeclareClrEnumType`, `DeclareClrRecordShell`,
  `DeclareClrInterfaceShell`, and the rest, in one switch. `DeclareClrShellsInsideModule`
  knows only about classes and nested modules, and `ModuleNeedsSourceReplay` allows only the
  same two. The top-level switch is the list the module path is missing.
- **Default parameters.** A top-level function with one emits under this same profile:
  `DeclareUserFunction` switches to packed arguments and `EmitUserFunctionBody` substitutes a
  missing-argument sentinel, evaluating the default expression in the body. That prologue is
  inline in `EmitUserFunctionBody` rather than shared, so the module path cannot call it.

### Why this stopped at the enumeration

The module shell's methods are not what tosh code calls. There are no IL call sites against
them — the shell exists "so external .NET callers can reflect over compiled tosh types", and
tosh-internal calls resolve through the engine, which today learns the module's contents from
the replayed source. Removing the replay therefore has to answer *where the engine learns
them instead*, and getting that wrong does not fail loudly: it makes a module's functions
silently unresolvable at run time, in a shell's own library.

That is a design question about the calling convention, not a missing case in a switch, and
it is the next thing this item needs.

## Decision 1 done — packed arguments, 2026-08-22

Chosen by the user over CLR-optional constants, because a default may be any expression and
theirs are not all constants: `span: TimeSpan = 1d` is not a CLR constant, and 67 defaulted
parameters were measured across the library.

A module method with an optional, rest, or defaulted parameter now takes its arguments packed
into one `object[]`, substitutes a missing-argument sentinel, and evaluates the default in its
own body — **the same shape a top-level function already used**. The prologue was inline in
`EmitUserFunctionBody`, which is exactly why the module path could not have it; it is now
shared, and the extraction was verified behaviour-neutral before anything else changed.

`ToshPackedArgumentsAttribute` marks the shape, because it is not inferable: a tosh function
of one array parameter emits the same signature.

### Three defects underneath it, none of which were failing before

Each was unreachable while these modules were replayed, and each became reachable the moment
they were not. That is the shape of this whole item.

1. **A module method with an expression body returned `null`.** `func Add(a, b) -> int =>
   $a + $b` computed its value, discarded it, and fell out through the implicit
   `return null`. The trailing-expression collapse that top-level functions and class methods
   both use was never applied to module methods. It emitted cleanly and answered nothing —
   **on both profiles**, so this was not a strict-mode problem.
2. **A dotted name in command position did not resolve.** `Probe.Add 1` reached the engine's
   command table by way of the replayed source; without replay it reported *"unknown command:
   'Probe.Add'"* while `Probe.Add(1)` resolved. `InvokeAndDrain` now asks the compiled shells
   first, the same order `InvokeQualifiedMethod` already used.
3. **A block argument inside a module method crashed the compiler, then the program.**
   `EmitBlockBodyMethod` adds a helper to `Program`, which was already created by the time
   module bodies were emitted — *"Unable to change after type has been created"*. Emitting
   module bodies first fixed that and exposed the next one: the helper was `Private`, and a
   module shell is a different type, so the first call threw `MethodAccessException`.

The corpus was strengthened for the same reason. `SourceReplaySurfaceTests` asserts a shape
**emits**; that is what let defect 1 hide, since a method that returns nothing emits
perfectly. `DifferentialExecutionTests` now carries four module cases asserting what they
*answer*.

## Decision 2 measured and not kept — 2026-08-22

The user chose "all seven declaration kinds, mirroring the top level". Mirroring the switch
is a small change and it was made: `DeclareClrShellsInsideModule` and
`ModuleNeedsSourceReplay` gained enum, record, interface, struct, trait and union.

**All six then emitted without replay, and all six failed at run time.** One root cause,
three faces of it:

```
record   No overload matched static method 'Point' on type 'p.M'
struct   unknown type 'M.Vec' in `new` expression
union    Static member 'Result' was not found on type 'p.M'
```

A shell is declared under its **bare** name and never wired to the module-qualified path
`ToshHost.ResolveQualifiedAccess` walks. Emitting the shell is not the missing piece; being
findable as `M.Point` is.

### The correction this forced

**A module-scoped *class* has the same defect, and always did.**
`ModuleNeedsSourceReplay` has accepted one since "step 1 of the first-class .NET plan", so
such a module already emits with no source carried — and the emitted program then fails with
*"unknown type 'M.Box'"*. Measured on the pushed commit as well, so it predates this work.

So the replay-free module path has never worked end to end for any declaration kind. What
`SourceReplaySurfaceTests` recorded as "emits" was true and was not the question worth
asking, which is the second time this item has been caught by that distinction — the first
was a module method that emitted and returned `null`.

The six were reverted rather than shipped: a kind that emits and crashes is worse than one
that replays and works. The class case is now a tripwire
(`A_module_scoped_class_emits_without_replay_and_then_fails`) so the defect cannot stay
invisible, and the module-method successes are asserted by running them rather than emitting
them.

### The prerequisite, built — module-qualified stamping

One line, and it fixes the defect that was already shipping. A shell for a type declared
inside a module is emitted as a top-level CLR type under its **bare** name, and
`RegisterCompiledAssembly` aliases it by `ToshOriginalNameAttribute` — which
`StampOriginalNameIfMangled` writes only when the CLR cannot spell the name. So `class Box`
inside `module M` was registered as `Box`, and nothing could ask for `M.Box`.

`StampModuleQualifiedName` writes it unconditionally. A qualified name is never what the CLR
type is called, so there is nothing to compare against.

| Kind | With the stamp |
|---|---|
| class | **works** — `new M.Box()` gives 5, matching interpreted |
| interface | **works** — a class fulfilling it answers 9 |
| enum | **works** — `M.Colour.Green` renders `Green`, after one more fix below |
| record | **works** — `new M.Point(3, 4)`, read back as 4 |
| struct | Its shell now works and `M.Vec` resolves as an annotation (`TOAST-0076`); the module path is still deliberately replayed until it is enabled and run end to end |
| trait | `M.Named` resolves as an annotation (`TOAST-0076`), but *"Member 'Name' was not found"* remains — trait members are not injected into the using class |
| union | `M.Result` resolves as an annotation (`TOAST-0076`), but `M.Result.Ok` still needs a factory on the base; variants are separate types |

Union was tried: stamping the base and every variant with the qualified name makes the type
resolve, and the call then fails one step later because `Ok` is looked up as a static method
on the base while the variant is a separate type. It needs factories, not names.

### Struct defaults — fixed 2026-08-24

Two causes, both silent.

**The constructor filled only the primary fields.** A `prop` carrying an initializer kept
whatever the runtime zero-writes, so `struct Pt { prop X: int = 1 }` read back `null`: `1`
interpreted, an empty line compiled, and nothing anywhere saying a value had gone missing.
The class path already pointed `_il` at the constructor's generator to emit initializers
through the ordinary expression machinery; the struct path now does the same, so a computed
default like `prop X: int = 2 + 3` works rather than only a literal one.

**A struct was not on the emitted structural-rendering path.** `TOAST-0022` admitted `class`
and `record` and left `struct` on the CLR-object fallback, rendering `p.Pt` — the assembly's
namespace, for a type the reader declared. A struct describes itself the way a class does,
`Pt { X = 1 }`, not the way a record does.

Worth noting: neither cause was about *emitting the struct*. The type, its fields and its
constructor were all emitted correctly. What was missing was the shell semantics wrapped
around them — defaults, and how the value describes itself.

The old struct result — "comes back as something `int` will not take" — was **not** a struct
defect at all. A module-qualified annotation was unresolved for `class`, `struct` and
`record` alike, and the diagnostic claimed the variable had no annotation when it plainly
did. [`TOAST-0076`](TOAST-0076.md) now resolves all three. Re-measuring trait and union
showed that their qualified annotations resolve too, but their failures remain after that
point: trait member injection and union variant factories are independent work.

### Enum needed a second piece, and it names the general shape

The stamp has to be written *during* declaration for an enum, because `DeclareClrEnumType`
closes the builder with `CreateType()` before returning — a stamp afterwards throws.

The rest is the more interesting half. `new M.Box()` resolves through the type-alias table,
which the stamp populates; `M.Colour.Green` does not. It is a **member chain**, and
`TryResolveCompiledModuleAccess` walked only the module shells — so it looked for a static
`Colour` on `M` and reported it missing. It now falls back to resolving the prefix as a
compiled *type* and reading the member from that.

So there are two resolution routes into a module, and a kind is only lifted when the route it
actually uses has been taught. That is why "mirror the switch" could not have worked, and it
is the question to ask of each remaining kind: `record` is reached as a *call*
(`M.Point(3, 4)`), `union` as a member chain like the enum, `struct` through `new`.

So the stamp is necessary and not sufficient: **four kinds are currently lifted — class,
interface, enum and record**. Struct's shell defaults, rendering and qualified annotation
are now fixed; it remains to enable the module path and pin the executed runtime-profile
answer. Trait still needs member injection, and union needs variant factories.

The record failure was a **bad probe rather than a defect**, and it is worth recording
because it nearly cost the record case. `M.Point(3, 4)` was written where a record wants
`new M.Point(3, 4)`, and the compiled program answered *"Construct instances with
'new M.Point(...)'"* — the compiler was right and the test was wrong. Every remaining
failure above was re-checked with the syntax the interpreter accepts. The three remaining
kinds are **left replaying** and are tripwires — a kind that emits and fails is worse than
one that replays and works, which is the lesson this item keeps teaching.

The measured library is unchanged at 2 of 16 — `Git` and `Math`. Enum being lifted does not
move it, because the files using enums (`Gl` 4, `Gtk` 5, `Sdl` 2) each carry other blockers
as well. Refinement types remain the largest single one at 18 uses, and are decision 3.

### Decision 3 measured — what a refinement actually needs — 2026-08-22

The user chose "emit the predicate as a method", following
`docs/refinement-types-dotnet-implementation.md`. Measuring first turned up a prerequisite
that document does not discuss, and it is not about predicates at all.

**A `type` alias inside a module was never on the module path** — neither
`DeclareClrShellsInsideModule` nor `ModuleNeedsSourceReplay` mentioned
`BoundTypeAliasStatement` — so both a refinement alias *and a plain one* replayed. Adding the
plain case is a two-line change and it emits. It then fails:

```
var d: M.Meters = 5
  -> 'd' produced a value that could not be converted to 'M.Meters'
```

`RegisterCompiledAssembly` registers a `[ToshType]` shell as a **type alias** — the name
`M.Meters` mapped to the shell class `p.Meters`. Converting `5` to that class is meaningless;
what the annotation needs is `M.Meters` meaning `int`. The alias shell already implements
`IShellRefinementTypeDescriptor` and carries `BaseTypeName`, and nothing reads it back.

So before a predicate can be emitted, **the runtime has to learn an alias from its compiled
shell rather than from replayed source**: name, base type, and — for a refinement — where its
check lives. That is the same registration a compiled refinement will need, so it is the
first piece of decision 3 rather than a detour from it.

### Built — the runtime reads an alias back from its shell

`ToshEngine.RegisterCompiledAliasType` is the entry point, because the lookup it feeds needs
the engine's own definition record and a compiled assembly cannot construct one.
`RegisterCompiledAssembly` calls it for every alias shell it finds, reading `BaseTypeName`
off the descriptor the shell already implemented.

A module-scoped **plain** alias now emits with no source carried and answers correctly:
`var d: M.Meters = 5` gives 5 on both backends. A **refinement** alias still replays,
deliberately — its predicate lives in the replayed source, so lifting the alias would take
the check with it.

That distinction had to be made visible in the metadata, and finding out why cost a
regression. The first version registered every alias shell, refinements included, as a
predicate-less alias — so `type PosInt = int where _ > 0 coerce (_ == 0 ? 1 : Math.abs(_))`
quietly stopped coercing and `-21` stayed `-42`. A shell is now stamped `alias` or
`refinement`, and only the first is registered. The suite caught it, which is the first time
in this item that a failure surfaced as a test rather than as a program crashing.

### What emitting the predicate actually needs — measured 2026-08-22

`BoundTypeAliasStatement` carries a `BoundExpression? Refinement`, which reads like the
predicate is already bound IR. It is not.

`RefinementClauseArgumentSyntax` — the clause model the parser builds, holding `where` and
`coerce` clauses with their guards — has **no case in `LowerExpression`**, so it falls to the
catch-all at the end:

```csharp
_ => new BoundDynamicExpression(expression, expression.Span),
```

whose comment says exactly what is happening, and what to do about it:

> shapes whose semantics are hard to flatten into a single bound node. Keep them dynamic —
> the lowering coverage on them can grow when (or if) the IL emitter needs structured access.

The IL emitter needs structured access now. `BoundNode.cs` mentions refinements exactly once,
and that is the field above: **there is no bound refinement clause model at all.**

So the design document's §1 is right about where this starts, and it starts further back than
"emit a method". In order:

1. Bound clause nodes — a `where` clause, a `coerce` clause, its optional guard — with `_` as
   a bound symbol rather than a name looked up in a pushed scope at evaluation time.
2. A `LowerExpression` case building them, which is what turns the catch-all comment's "when
   the IL emitter needs structured access" into that day.
3. Emitted `IsValid` / `Convert` methods per the document's §2, using the ordinary expression
   emitter.
4. A registration carrying pointers to those methods, extending
   `RegisterCompiledAliasType`, which today registers a base type and nothing else.
5. Engine dispatch that calls the compiled check, and a differential case proving the
   compiled and interpreted checks agree on the same value — including the coercion path,
   whose base-type re-conversion `TOAST-0068` had to add first.

Steps 1 and 2 are binder work and are the bulk of it. Nothing above needs the emitter until
step 3.

### What the document gets right, and what it assumes

Its core principle matches this item exactly — predicates and coercers become bound IR and
ordinary methods, and the artifact needs no interpreter. Its canonical algorithm was already
worth acting on: the missing base-type re-conversion it predicts in §3 was a live defect,
fixed as `TOAST-0068`.

Two of its assumptions do not hold here and are worth writing down before building on it:

- **§1 assumes a statically-typed binder** (`BindExpression(expectedType:)`,
  `RequireImplicitConversion`) and asks that `_` become a real bound parameter symbol.
  `BoundTypeAliasStatement` already carries a lowered `BoundExpression`, so this is closer
  than it looks — but `_` is bound by the interpreter's refinement machinery today, not as a
  parameter.
- **"Closed except for `_`" would reject the library it was written about.**
  `Math.Clamp(_, 0, 100)` and `Math.abs(_)` are free references to module functions, so
  `EnsureClosedExpression(..., except: valueParameter)` as specified throws out
  `MathTypes.tosh` wholesale. The rule wants relaxing to "closed except `_` and resolvable
  module functions" — newly practical, since module methods began emitting in this item.

It also omits the constraint this repository enforces: a compiled `IsValid` and the
interpreter's engine-run predicate must agree on the same value, in the differential corpus.

### Where the measured library stands

Two of sixteen files now emit with no source replay, against one before. The remaining
fourteen are decisions 2 and 3 — declaration kinds, and refinement types, which are the most
used blocking construct at 18.

## Acceptance

- [x] Every "unsupported" emitter diagnostic is enumerated with a program that triggers it —
      `SourceReplaySurfaceTests`, as a corpus plus tripwires
- [ ] Each is either implemented, or recorded as a deliberate and documented exclusion —
      **implemented**: default, rest and optional parameters; module-scoped classes,
      interfaces, enums and records; plain `type` aliases; computed properties on class
      shells. **Remaining**: struct, trait, union, and refinement types, each recorded above
      with what it reported — none of them dropped, all of them still wanted
- [ ] `--compile-allow-dynamic` is not needed by any program in `examples/` or `bench/`
- [ ] A strict profile fails the build rather than replaying source
- [ ] A negative control

## Why it is worth finishing

Raised by the user asking whether compiling a library would pay off. Measured, the answer is
"yes, but not yet, and for a reason worth stating": the library files that *do* compile embed
their module bodies as source and re-evaluate them through the interpreter at load, so a
compiled library pays the same parse cost from a string in a DLL instead of a file on disk.
`Point.tosh` compiles, and `IPoint` appears eleven times in the emitted assembly as a UTF-16
string constant.

The prize is measurable. That machine's library costs about 100 ms of a 320 ms shell
start-up, and the cost tracks line count — roughly 0.07 ms per line across files from 166 to
718 lines — which is parse-and-bind, not FFI. Removing source replay is what would make that
compilable away. `Sdl.tosh`'s `bind native "libSDL2-2.0.so.0"` would remain.

## Notes

`TOAST-0030` closed the one replay path that was actively wrong. This item is about the
mechanism rather than a single use of it.
