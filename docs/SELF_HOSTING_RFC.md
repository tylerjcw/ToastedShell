# Self-Hosting Tōast — Architecture RFC

**Status:** Exploratory design. Not scheduled. Assumes Tōast and TōSh are
finished, stable, and supported by a comprehensive conformance corpus before
bootstrap work begins.

## Summary

Tōast can become a self-hosting language with two compilation targets and one
front end:

```text
Tōast source
    │
    ▼
syntax → binding → typed semantic IR → lowered target-neutral IR
                                      ├── .NET IL backend
                                      └── native C backend → system C compiler
```

The language owns its value model, core types, observable semantics, metadata,
and portable standard library. The .NET runtime and the C ABI are peer foreign
systems:

- the .NET target provides first-class CLR interop;
- both targets provide first-class native C interop;
- the native target does not load or depend on the CLR.

The bootstrap proceeds through IL before native code:

1. The existing C# compiler compiles a compiler written in Tōast.
2. That compiler compiles itself to IL until it reaches a reproducible fixed
   point.
3. The self-hosted compiler gains a C emitter and targets the native runtime.
4. The resulting native compiler compiles itself without .NET.

Self-hosting means that the compiler and portable libraries are written in
Tōast. A small foreign substrate remains necessary for allocation, garbage
collection integration, operating-system entry points, and C ABI operations.

## Goals

- One language, grammar, type system, semantic model, and front end.
- A self-hosted compiler that can compile itself to IL and native code.
- Tōast-owned core types whose behavior does not depend on CLR definitions.
- A native runtime that has no .NET installation or startup dependency.
- First-class CLR interop on the .NET target.
- First-class C ABI interop on both targets.
- Deterministic compiler behavior and cross-backend semantic conformance.
- A gradual path that improves the existing compiler before native work begins.
- A portable language even where TōSh remains platform-specific.

## Non-goals

- Replacing the existing compiler or runtime before Tōast is mature.
- Treating the native target as a second language or dialect.
- Reimplementing the CLR, BCL, or arbitrary NuGet packages for native mode.
- Making a native executable carry arbitrary CLR objects without running a CLR.
- Writing a garbage collector, linker, object-file writer, or machine-code
  backend as the first native implementation.
- Rewriting TōSh, the LSP, DAP, MCP server, and every tool before the compiler
  can self-host.
- Using assembly as the implementation language for the compiler or runtime.

## Compilation targets and profiles

Target selection and capability selection are separate concepts:

```text
--target dotnet --profile clr
--target dotnet --profile no_clr
--target native
```

`--target native` implies `no_clr`.

### `dotnet` with `clr`

- Emits .NET IL.
- Uses CLR garbage collection and managed runtime services.
- May load assemblies and use CLR reflection.
- Exchanges Tōast and CLR values through the .NET bridge.
- Uses the C ABI through native interop.

### `dotnet` with `no_clr`

- Emits .NET IL as a bootstrap and verification target.
- Uses the CLR only as its physical execution host.
- Rejects source and dependencies that require CLR-specific capabilities.
- Observes the same portable semantics required by the native target.
- Proves portable-source separation before native code generation exists.

### `native`

- Emits C from the same lowered IR used by the IL backend.
- Invokes the platform C compiler and linker.
- Links the Tōast native runtime and selected portable libraries.
- Provides Tōast reflection metadata without CLR reflection.
- Supports the C ABI but rejects CLR capabilities.
- Runs without CoreCLR, `hostfxr`, managed assemblies, or a .NET installation.

## Capability model

`no_clr` is enforced by the binder and verifier, not by scanning source text
for forbidden imports. Capabilities are attached to symbols, calls, types,
modules, intrinsics, and foreign values in the typed semantic IR.

The initial capability set includes:

| Capability | Meaning |
|---|---|
| `core` | Portable Tōast semantics available on every target |
| `os` | Portable operating-system abstraction implemented per platform |
| `native` | C ABI calls and native layout operations |
| `unsafe` | Unchecked pointers, raw memory, and lifetime-sensitive operations |
| `clr` | CLR types, reflection, assemblies, managed callbacks, or managed APIs |

A `no_clr` compilation verifies the transitive dependency graph. It rejects:

- direct use of CLR types or namespaces;
- `load-assembly` and CLR reflection;
- calls to functions whose effects include `clr`;
- generic instantiations whose implementation requires `clr`;
- dynamic values whose origin or possible operations include CLR objects;
- portable modules that expose CLR-backed values through `Any` or an
  apparently portable interface.

The diagnostic identifies the capability, the operation that introduced it,
and the dependency path by which it reached the `no_clr` compilation.

## Architectural layers

The core is divided into four layers so language semantics are not confused
with runtime machinery.

### Runtime kernel

The kernel supplies operations that cannot initially be ordinary Tōast:

- process and module startup;
- allocation and garbage-collector integration;
- object headers and type descriptors;
- primitive arithmetic and conversion intrinsics;
- string and contiguous-storage allocation;
- write barriers and reference tracing;
- exception propagation and stack metadata;
- scheduler and suspension hooks;
- C ABI calls, callbacks, and raw layout;
- operating-system calls needed by the portable platform layer.

The kernel is managed code for the .NET target and a small C runtime for the
native target.

### Portable core library

The public core API is written primarily in Tōast and is identical across
targets:

- collection APIs and algorithms;
- iterators and streams;
- `Option`, `Result`, and errors;
- comparison, hashing, formatting, and parsing;
- numeric helpers and Unicode text algorithms;
- core reflection descriptors;
- task, future, and channel protocols.

Storage, allocation, and a small number of hot operations may be intrinsics,
but public behavior is defined by Tōast source and the language specification.

### Portable standard library

The standard library contains higher-level types and services:

- regular expressions;
- temporal and calendar types;
- paths, filesystem, serialization, and structured data;
- URI, IP, and identifier types;
- quantities, complex numbers, vectors, and matrices;
- compression, cryptography, networking, and process APIs where portable
  implementations or stable platform adapters exist.

### Platform and interop libraries

Target-specific libraries expose intentionally foreign capabilities:

- CLR types, assemblies, delegates, tasks, and reflection;
- C pointers, handles, callbacks, calling conventions, structs, and unions;
- POSIX, Win32, and other platform-specific facilities;
- terminal, job-control, process, and signal behavior used by TōSh.

## Core value model

Tōast defines observable behavior. Backends may use different physical
representations when they preserve the same semantics.

### Fundamental values

| Type | Contract |
|---|---|
| `never` | Has no values; used for expressions that cannot complete normally |
| `nothing` | The single successful no-result value; internally `Unit` |
| `null` | The absence singleton; distinct from `nothing` |
| `bool` | `true` or `false` |
| `Any` | A boxed value whose concrete Tōast type is known at runtime |
| `Type` | A Tōast type descriptor independent of CLR `System.Type` |
| `Error` | The root of language-level exceptional values |

`void` is an ABI concept for functions returning no machine value. A Tōast
function that completes without producing a result has type `nothing`.

Portable typed code uses explicit nullability. `T?` admits `null`; an ordinary
`T` does not. `Any` may contain `null` in dynamic code. `Option<T>` represents
domain-level optionality, and `Result<T, E>` represents expected failure.

`object` may remain an ergonomic spelling for `Any`, but it does not require
every value to have heap identity. Machine integers and booleans remain unboxed
until a dynamic boundary requires boxing.

### Integer types

The portable integer family has explicit widths:

- `i8`, `i16`, `i32`, `i64`, `isize`
- `u8`, `u16`, `u32`, `u64`, `usize`

Familiar names remain aliases:

| Alias | Canonical type |
|---|---|
| `sbyte` | `i8` |
| `byte` | `u8` |
| `short` | `i16` |
| `ushort` | `u16` |
| `int` | `i32` |
| `uint` | `u32` |
| `long` | `i64` |
| `ulong` | `u64` |

Integer literals default to `int` when representable, then to a wider type
according to suffix and value. Arithmetic overflow is checked by default on
every backend. Wrapping and saturating operations are explicit. Division,
remainder, shifts, and narrowing conversions have one specified behavior.

`isize` and `usize` are for storage sizes, indices, pointer arithmetic, and ABI
boundaries. Ordinary arithmetic prefers fixed-width types so behavior does not
change with target architecture.

### Floating-point and decimal types

| Type | Contract |
|---|---|
| `f32` / `float` | IEEE 754 binary32 |
| `f64` / `double` | IEEE 754 binary64 |
| `decimal` / `dec` | Base-10 value with Tōast-defined decimal semantics |

The specification defines NaN comparison and hashing, signed zero, infinity,
rounding, parsing, and shortest round-trip formatting.

`decimal` uses a sign, a 96-bit coefficient, and a scale from 0 through 28.
Operations are checked and use round-to-nearest-even unless another mode is
named. This is compatible with `System.Decimal` without making the BCL its
normative definition. Native code may use 128-bit integer operations internally.

`BigInt` and `Complex` are portable-library types rather than primitives.

### Text and binary values

| Type | Contract |
|---|---|
| `rune` | A valid Unicode scalar value |
| `string` / `str` | Immutable Unicode text |
| `bytes` | Immutable binary data |
| `buffer` | Mutable binary storage |

Native strings use validated UTF-8. The .NET implementation may use
`System.String`, but observable operations follow the Tōast Unicode contract.
Crossing the CLR boundary performs required UTF-8/UTF-16 conversion.

Text APIs distinguish byte, rune, and grapheme length and iteration. String
indexing, where accepted, addresses Unicode scalar positions and never exposes
UTF-8 bytes or UTF-16 code units. Byte-oriented algorithms use `bytes`, and
grapheme-aware display and editing use explicit grapheme APIs.

Core parsing, comparison, and serialization are culture-invariant. Locale-aware
behavior is explicit. String containment and default comparison are ordinal.

Compiler source spans use UTF-8 byte offsets. A source line map translates byte
offsets to line, column, rune, and display positions.

`char16` exists only where CLR or foreign ABI interop requires a UTF-16 code
unit. It is not the portable text atom.

### Collections and products

| Type | Contract |
|---|---|
| `array<T>` | Fixed-length contiguous mutable storage |
| `list<T>` | Growable mutable sequence |
| `slice<T>` | Bounds-checked view over contiguous storage |
| `tuple<T...>` | Immutable fixed-arity structural product |
| `dict<K, V>` | Insertion-ordered hash dictionary |
| `set<T>` | Insertion-ordered unique collection |
| `range` | Integer range as defined by its syntax |
| named `record` | Nominal named-field product |
| anonymous record | Immutable structural named-field product |
| `Row` | Dynamic insertion-ordered string-field object for shell data |

`dict<K, V>` accepts keys satisfying `Hash` and `Eq`; it is not limited to
strings. Iteration order is insertion order, ensuring stable shell output and
compiler behavior independently of table layout or hash randomization.

Mutable collections have reference identity. Tuples and immutable records use
structural equality. A named record remains nominal when another record has the
same fields. `Row` is the explicit escape hatch for late-bound fields and
schema-changing pipeline data.

Language ranges remain integer-based. Other progressions belong in portable
libraries rather than changing range-literal semantics.

Slices retain or otherwise protect their owner. A safe slice cannot outlive its
storage or become invalid after collection growth. Borrowed raw spans used by
interop are `unsafe` and cannot escape their verified scope.

### User-defined type forms

- `class` defines a nominal reference type with identity and inheritance.
- `record` defines a nominal data type with value-oriented equality.
- `struct` defines a nominal value type with explicit layout only when requested.
- `union` defines a closed tagged sum suitable for exhaustive matching.
- `enum` defines a closed named set with a specified ABI representation where
  required.
- `interface` defines a behavioral contract and dynamic dispatch surface.
- Generic constraints are Tōast protocols, not CLR interface tests.

Closed compiler trees prefer unions and records because matches can be
exhaustive and native layouts are predictable. Open application object models
may use classes and interfaces.

### Core protocols

The portable core defines at least:

- `Eq`
- `Hash`
- `Comparable<T>`
- `Formattable`
- `Iterable<T>`
- `Iterator<T>`
- `Stream<T>`
- `Callable<Args..., Result>`
- `Future<T>`
- `Disposable`

Equality, containment, hashing, truthiness, conversion, and formatting route
through one semantic implementation per target and one shared conformance
corpus. String containment is ordinal; dictionary containment tests keys; and
collection containment uses canonical Tōast equality.

### Pipelines, iteration, and asynchronous values

A collection is one scalar value. Iteration and streaming are explicit:

- `Iterable<T>` supplies synchronous pull-based iteration.
- `Stream<T>` supplies asynchronous values over time.
- `Future<T>` supplies one asynchronous completion.
- `Channel<T>` supplies coordinated asynchronous communication with an
  explicit closed/end result.

Function execution carries an output stream and a completion result. Emitted
values remain visible when a later `return` terminates the function, and a
return value may contribute one final value according to the function contract.

The compiler never guesses stream cardinality because a value is enumerable.
Adaptation from a collection to a stream is explicit in the bound tree even
where pipeline syntax makes it concise.

### Errors and diagnostics

`Error` is a Tōast object with a stable identifier, message, cause, structured
data, and portable stack frames. Foreign exceptions are wrapped with their
identity and details preserved by the interop layer.

`Diagnostic` is data rather than an exception. It contains a stable diagnostic
ID, severity, message parameters, source span, related spans, notes, and
optional fix information.

Expected lexer, parser, binder, and type-checker failures accumulate diagnostics
and return `Result`. Exceptions represent internal compiler faults or explicit
language throws.

`defer` executes all cleanup actions in last-in, first-out order on normal
completion, return, throw, break, and cancellation. Cleanup failures are
preserved in execution order rather than stopping remaining cleanup.

### Time and calendar types

| Type | Meaning |
|---|---|
| `date` | Civil calendar date without time or zone |
| `time` | Wall-clock time without date or zone |
| `LocalDateTime` | Civil date and time without a global instant |
| `Instant` | A point on the UTC timeline |
| `OffsetDateTime` | Date and time paired with a numeric UTC offset |
| `duration` | Fixed elapsed time |
| `Period` | Calendar-relative amount such as months or years |
| `TimeZone` | Named timezone rules supplied by the standard library |

Intrinsic date literals remain exact ISO forms. Parsing and arithmetic are
specified independently of `System.DateTime`, and timezone database behavior is
versioned by the standard library.

### Regular expressions

`regex` uses a specified portable Tōast dialect. Syntax, matching, capture,
replacement, Unicode, and option behavior are identical across targets.

The portable implementation favors a Tōast-written NFA engine for the regular
subset. Backtracking, backreferences, and advanced lookaround are included only
when their complexity and worst-case behavior are deliberately accepted.
CLR-specific regex remains a foreign CLR value in the `clr` profile and does
not define portable semantics.

## Runtime implementation model

### Intrinsics and the core manifest

Public core types and methods are declared in Tōast source. Irreducible runtime
operations use stable intrinsic IDs rather than backend class or method names.

A versioned core manifest records:

- canonical type and intrinsic IDs;
- public names and aliases;
- generic arity and constraints;
- layout class and ABI requirements;
- capability requirements;
- compiler-known lowering rules;
- conformance version.

The bootstrap compiler consumes a precompiled core module. Both runtimes
implement the manifest, while ordinary methods compile from shared Tōast source.
For example, list algorithms and formatting are Tōast, while allocation,
capacity growth, and GC barriers are intrinsics. Unicode algorithms are mostly
Tōast, while validated string allocation may be intrinsic.

### Static and dynamic representation

Statically typed operations use unboxed values where possible. Dynamic
boundaries box values into a simple tagged representation:

```text
Value
├── tag and flags
└── immediate payload or managed object pointer
```

The initial native representation should use two machine words rather than NaN
boxing. Small integers, booleans, `nothing`, and `null` can be immediate.
Strings, collections, classes, closures, and boxed structs use heap objects.

Boxing occurs at explicit boundaries:

- conversion to `Any`;
- heterogeneous collections;
- dynamic shell pipelines;
- reflection and dynamic dispatch;
- foreign interop.

The representation is an internal ABI detail. Public native APIs use opaque
handles or versioned ABI structures so layout optimization does not break
extensions.

### Object and type layout

A native heap object contains a compact header followed by its payload. The
header identifies a Tōast type descriptor and carries collector state or flags.

A type descriptor contains:

- stable type identity and display name;
- size, alignment, and GC reference map;
- base type and implemented interfaces;
- generic arguments;
- member and method metadata;
- equality, hashing, comparison, conversion, and formatting operations;
- dispatch tables;
- source and debug metadata where retained.

Reflection is a Tōast facility. `type-of`, `members`, dynamic calls, debugger
inspection, and shell display use emitted Tōast metadata. Compilation may offer
metadata-retention levels, but shell and tooling builds retain the complete
dynamic surface.

### Generic implementation

The native backend initially monomorphizes generic code whose value layouts need
specialization. It may share code for compatible reference representations when
type descriptors preserve generic arguments. Interface calls use emitted
witness or dispatch tables.

The IL backend may use CLR generics as an implementation technique, but Tōast
defines constraint checking, variance, equality, and reflection behavior.

### Garbage collection and resources

The first native runtime uses the Boehm collector behind a narrow allocation
interface. It supports cycles, is cross-platform, and avoids making a custom
collector a prerequisite for self-hosting.

All managed allocation and root registration pass through the runtime ABI so a
future precise collector can replace Boehm without changing Tōast source or the
public object model.

Scarce resources use deterministic disposal. Files, sockets, terminal state,
foreign handles, and locks are released through `Disposable`, lexical cleanup,
or `defer`; GC finalization is only a fallback.

### Control flow, exceptions, generators, and async

The target-neutral lowered IR explicitly represents:

- normal and exceptional exits;
- cleanup regions and ordered `defer` execution;
- catch and finally edges;
- generator suspension and resumption;
- async suspension, cancellation, and completion;
- function output streams separately from completion values.

The IL backend maps these operations to CLR mechanisms. The C backend lowers
them to state machines, landing pads, and runtime unwind frames. Generated C
does not depend on C++ exceptions.

### Module initialization and startup

The compiler emits an explicit initialization graph. Modules initialize once in
dependency order, detect initialization cycles, and distinguish compile-time
data from runtime initialization.

The native runtime is statically linked where practical and performs no package
resolution, framework probing, managed assembly loading, or JIT compilation at
startup. Native shell and command-line binaries target single-digit-millisecond
startup on supported systems.

## Interoperability

### Native C ABI

Native interop remains explicit about layout, ownership, and safety:

- `ptr<T>` and `mut_ptr<T>` for borrowed raw pointers;
- `handle<T>` for opaque owned or reference-counted foreign handles;
- `cstr` and explicit-length byte or string views;
- fixed-size arrays and ABI-specific scalar aliases;
- `raw struct` and `raw union` for C-compatible layout;
- calling conventions and variadic restrictions;
- `bind native` and raw callbacks;
- explicit allocation, pinning, copying, and release behavior.

Portable objects never acquire C layout accidentally. Crossing the ABI uses an
explicit raw representation or generated marshalling adapter. Unsafe pointers
cannot be stored in safe values without an ownership wrapper.

Library resolution accounts for platform naming and ABI differences without
embedding Linux-specific library names in portable modules.

### .NET bridge

The .NET bridge exposes CLR objects as foreign values carrying the `clr`
capability. Surface syntax may remain concise, including fluent member access,
but the binder retains the distinction between a Tōast type and a CLR type.

The bridge specifies conversions for:

- Tōast strings and `System.String`;
- numeric types, including checked narrowing and decimal conversion;
- arrays, lists, dictionaries, and enumerable adapters;
- delegates and Tōast callables;
- `Task<T>` and `Future<T>`;
- `IAsyncEnumerable<T>` and `Stream<T>`;
- CLR exceptions and Tōast `Error`;
- managed object identity and GC handles;
- nullable values and `Option<T>`;
- reflected metadata and Tōast type descriptors.

Conversions are explicit in the bound tree even where syntax inserts them
implicitly. Collection adapters document whether they copy, wrap, pin, or
stream. A CLR object never silently becomes a portable Tōast object.

## Compiler architecture

### Shared front end

The self-hosted compiler is a library with a thin command-line host. Its passes
are:

1. source loading and Unicode validation;
2. lexing;
3. parsing into a lossless syntax tree;
4. declaration and module binding;
5. name and overload resolution;
6. type, flow, capability, and effect checking;
7. lowering into typed target-neutral IR;
8. semantics-preserving optimization;
9. IL or C emission;
10. artifact, metadata, and diagnostic production.

The interpreter consumes the same bound representation where practical. It is
not an independent definition of language semantics.

### Compiler data model

The compiler-writing subset supports strongly typed forms of:

- `SourceText`, `SourceSpan`, and line maps;
- `Diagnostic` and `DiagnosticBag`;
- `TokenKind` and `Token`;
- closed syntax-node unions;
- symbols and parent-linked scopes;
- Tōast type and constraint representations;
- bound-node unions and control-flow graphs;
- lowered IR instructions and blocks;
- modules and dependency graphs;
- compiler options and target profiles;
- artifacts and emitted metadata;
- `Result<Artifact, list<Diagnostic>>`.

Tokens retain source spans instead of allocating a string for every lexeme.
Trees are predominantly immutable. Scopes use typed maps with parent links
rather than cloned dynamic hash tables. Expected source errors accumulate
diagnostics; internal invariant failures throw compiler errors.

### Backend contract

Both backends consume the same lowered IR and intrinsic contract.
Backend-specific nodes are isolated after semantic checking and cannot redefine
language behavior.

The IL backend emits managed metadata and resolves intrinsic IDs to the managed
runtime. The C backend emits readable C, source mappings, module metadata, and
calls into the native runtime ABI.

The C backend delegates optimization, object-file generation, debug format,
relocation, and linking to the platform toolchain. LLVM or direct machine-code
emission may be added later without changing the front end or semantic IR.

### Compiler-subset requirements

Every feature used by the compiler must compile without source replay or calls
back into the interpreter. Runtime calls are permitted only through stable core
intrinsics available on both runtimes.

The readiness gate requires:

- no source replay;
- no `ToshEngine` dependency in compiler artifacts;
- no CLR-only host dispatch in `no_clr` modules;
- no implicit dynamic locals or members in compiler code;
- no backend-specific semantic fallback;
- all required behavior represented in typed and lowered IR.

## Compiler-shaped readiness probes

[`bench/probes/compiler_shape.tosh`](../bench/probes/compiler_shape.tosh)
contains a lexer, parser, AST hierarchy, visitors, evaluator, scoped variables,
and structured lex and parse errors. Its interpreted cases produce:

```text
1 + 2 * 3                              → 7
(1 + 2) * 3                            → 9
-x + y * 2                             → -2
let a = x * 2 in a + y                 → 24
let a = 1 in let b = 2 in (a + b) * x  → 30
```

This establishes that Tōast can express compiler-shaped code. It is an
expressiveness probe rather than the portable gate because it contains implicit
dynamic types, an unannotated compile boundary, `System.Collections.Hashtable`,
and allocation-heavy substring and list-concatenation patterns.

The portable readiness probe requires:

- no `System.*` references or CLR-derived values;
- fully annotated parameters, fields, locals, and returns;
- typed token kinds and source spans;
- a closed typed syntax tree;
- a typed symbol table and parent scopes;
- structured diagnostic accumulation and a typed compile result;
- Unicode, invalid-source, large-input, and deep-tree cases;
- interpreted and compiled semantic equivalence;
- clean `dotnet/no_clr` compilation without source replay;
- a negative case proving that CLR capability leakage is rejected.

Native readiness additionally requires:

- compilation and execution against the native runtime;
- identical diagnostics and results under both backends;
- stable generated IR and C for identical inputs;
- allocation and throughput budgets representative of compiler workloads;
- sanitizer and leak checks for the native runtime.

## Bootstrap

### Prerequisites

Bootstrap begins only after:

- portable core semantics and their conformance corpus are authoritative;
- the compiler subset compiles without interpreter fallback;
- the interpreter and IL backend pass the differential corpus;
- the canonical semantic and lowered IRs support both emitters;
- `no_clr` verification passes the portable probe;
- diagnostics, modules, generics, unions, interfaces, higher-order calls,
  bitwise operations, and deep recursion are production-ready.

### IL bootstrap

```text
Existing C# compiler
        │ compiles
        ▼
Tōast compiler IL-0
        │ compiles its own source
        ▼
Tōast compiler IL-1
        │ compiles its own source
        ▼
Tōast compiler IL-2
```

IL-1 and IL-2 produce equivalent artifacts. Reproducible build inputs eliminate
timestamps, random identifiers, absolute paths, unstable hash iteration, and
environment-dependent metadata so artifacts can be compared directly.

The IL bootstrap establishes self-hosting independently of native work. It also
exercises the compiler on a large typed program while CLR debugging and tooling
remain available.

### Native bootstrap

```text
Self-hosted IL compiler
        │ emits C for compiler and portable core
        ▼
System C compiler builds native N-0
        │ compiles its own source
        ▼
Native compiler N-1
        │ compiles its own source
        ▼
Native compiler N-2
```

N-1 and N-2 emit equivalent semantic IR and C. Binary comparison is required
when the selected C toolchain supports reproducible output; otherwise toolchain
build IDs and platform metadata are separately accounted for.

The native kernel and system C toolchain form the bootstrap substrate. They do
not prevent the compiler and portable library from being self-hosted.

## Delivery sequence

### Phase A — Specify portable semantics

- Define the core value model and source-level contracts.
- Establish the core manifest and intrinsic boundary.
- Create conformance tests independent of BCL names and behavior.
- Resolve equality, hashing, ordering, nullability, overflow, Unicode,
  formatting, collection shape, streaming, and exception semantics.
- Implement those semantics on the existing .NET runtime.

Related plan item: [`TS-P3-16`](plan/items/TS-P3-16.md).

**Exit:** core behavior is specified in Tōast terms and enforced by a
backend-neutral corpus.

### Phase B — Make compiler-shaped code production-ready

- Complete type-system support needed by compiler data structures —
  [`TOAST-0034`](plan/items/TOAST-0034.md).
- Remove compiler-subset source replay and implicit dynamic fallbacks —
  [`TOAST-0035`](plan/items/TOAST-0035.md).
- Make higher-order calls, interfaces, unions, narrowing, generics, and method
  references reliable — [`TOAST-0036`](plan/items/TOAST-0036.md).
- Define compiler diagnostics and performance budgets —
  [`TOAST-0037`](plan/items/TOAST-0037.md).
- Establish the typed portable readiness probe —
  [`TOAST-0038`](plan/items/TOAST-0038.md).

**Exit:** the probe compiles and runs through the normal IL path without an
interpreter dependency.

Each bullet was **measured before it was filed**, on 2026-08-21, and two came back
materially different from their wording. The first is not "incomplete type-system
support" but a declared type going unused: `func f() -> int` followed by
`var f_result = f()` reports that the type could not be pinned down. The third
implied six unreliable features, and four of the six already compile — what is
missing is a concrete function type, so no higher-order value can be annotated at
all.

Compiled-backend semantics were separately corrected under
[`TOAST-0030`](plan/items/TOAST-0030.md), which took the differential corpus from
nine recorded divergences to three.

### Phase C — Establish backend-neutral compilation

- Freeze the canonical bound tree and lowered IR contracts.
- Make interpreter and IL behavior pass the differential corpus.
- Define and enforce the `no_clr` capability graph.
- Move compiler-subset builtins, default parameters, annotated writes,
  refinement behavior, and regex onto portable runtime contracts.

Related plan items:

- [`TS-P3-15`](plan/items/TS-P3-15.md)
- [`TS-P3-17`](plan/items/TS-P3-17.md)
- [`TS-P3-18`](plan/items/TS-P3-18.md)
- [`TS-P3-19`](plan/items/TS-P3-19.md)
- [`TS-P3-20`](plan/items/TS-P3-20.md)

**Exit:** the compiler subset passes `dotnet/no_clr`, has no source replay, and
has no backend-specific semantic path.

### Phase D — Self-host on IL

- Write the compiler library and thin CLI in Tōast.
- Build it with the existing compiler.
- Execute the IL-0 → IL-1 → IL-2 bootstrap.
- Establish reproducible compiler output.
- Run the complete language and diagnostic corpus under the self-hosted compiler.

**Exit:** Tōast compiles its own compiler to a stable IL fixed point.

### Phase E — Build the native runtime

- Implement the versioned runtime ABI and core manifest.
- Integrate Boehm GC behind the allocation interface.
- Implement `Value`, object headers, type descriptors, strings, arrays,
  collections, closures, errors, metadata, and module initialization.
- Implement state-machine and unwind support.
- Port the portable core and the compiler's required standard-library subset.
- Establish startup, memory, sanitizer, and conformance gates.

Related plan item: [`TS-P3-21`](plan/items/TS-P3-21.md).

**Exit:** target-neutral compiler IR executes against the native runtime without
CLR components.

### Phase F — Emit C and bootstrap natively

- Implement the C backend over the shared lowered IR.
- Emit source mappings and versioned runtime calls.
- Build with supported platform C toolchains.
- Execute the N-0 → N-1 → N-2 bootstrap.
- Compare semantic IR, generated C, diagnostics, and reproducible binaries.

Related plan item: [`TS-P3-22`](plan/items/TS-P3-22.md).

**Exit:** the native compiler compiles itself without .NET.

### Phase G — Port TōSh and tooling incrementally

- Move portable standard-library modules to Tōast.
- Separate shell-specific OS capabilities from the language core.
- Build native TōSh components after their runtime dependencies exist.
- Reuse the compiler library from the LSP, formatter, documentation generator,
  package tooling, MCP server, and DAP.
- Retain the .NET target and bridge as a supported peer target.

**Exit:** each tool selects the target appropriate to its capabilities and
deployment needs without forking the language.

## Verification strategy

### Semantic conformance

Every core operation has backend-independent positive, negative, boundary, and
property tests. The same corpus runs against:

- the interpreter;
- IL with the `clr` profile;
- IL with the `no_clr` profile;
- the native backend.

The corpus includes Unicode boundaries, numeric overflow, decimal rounding, NaN
and signed zero, equality and hashing, ordered collections, exception cleanup,
async cancellation, reflection metadata, and foreign conversion behavior.

### Differential execution

The harness compares yielded values and order, completion status, stdout and
stderr, structured diagnostics, source spans, exceptions, cleanup failures, and
serialized reflection metadata. Target-specific behavior requires an explicit
capability and target-specific test.

### Reproducible bootstrap

Compiler stages use stable source ordering, dictionary iteration, path mapping,
timestamps, identifiers, locale, timezone, target versions, flags, and C
toolchain identity. Fixed-point checks compare normalized artifacts, semantic
IR, generated C, and diagnostics.

### Native runtime verification

Native tests use platform sanitizers and stress:

- allocation and collection with cyclic graphs;
- roots across callbacks, threads, and suspension points;
- exception unwinding and ordered cleanup;
- malformed foreign text and Unicode validation;
- collection mutation during iteration;
- foreign ownership, pinning, and callback lifetime;
- deep syntax trees and large compiler inputs;
- module initialization cycles;
- ABI compatibility across runtime versions.

## Performance and distribution

Native compilation is justified primarily by deployment independence,
embedding, predictable latency, and control over representation. Repeated shell
invocation can also use a resident process, so startup alone does not determine
whether a native target is worthwhile.

Native release gates include:

- single-digit-millisecond startup for a minimal command on supported systems;
- explicit memory-floor and compiler peak-memory budgets;
- compiler throughput on representative projects;
- pipeline throughput for structured objects and text;
- binary-size budgets for minimal, compiler, and shell profiles;
- no runtime package resolution or JIT dependency;
- self-contained distribution where platform policy permits it.

Representation optimizations, a precise collector, code sharing, direct
machine-code emission, and vectorized kernels remain implementation choices and
cannot change language semantics.

## Cross-platform model

The language, compiler, runtime, and TōSh have separate portability milestones.
The C backend and runtime target supported platform ABIs. TōSh additionally
requires terminal, process, signal, job-control, and filesystem behavior that
differs substantially across operating systems.

The portable platform layer defines stable capabilities and data types. Platform
adapters implement them through POSIX, Win32, or another native API. A module
requiring a platform capability declares it and cannot be mistaken for portable
`no_clr` code.

## Risks

### Semantic divergence

Two runtimes can drift in Unicode, numeric conversion, reflection, exceptions,
and collections. The core specification, shared Tōast source, and differential
corpus are release gates.

### Runtime and library scope

Strings, metadata, exceptions, async execution, and garbage collection are a
larger effort than C emission. The kernel remains small, with higher-level
behavior in portable Tōast. Native libraries are prioritized by the compiler
and portable tooling instead of attempting immediate BCL parity.

### Dynamic-language pressure

Unrestricted `Any`, reflection, and late-bound access can hide CLR dependencies
and force pervasive boxing. The typed compiler subset, capability tracking, and
emitted metadata keep dynamic behavior explicit without removing shell
ergonomics.

### Garbage-collector limitations

A conservative collector may retain objects falsely and cannot match all CLR
collector behavior. The runtime ABI permits a later precise collector, while
deterministic disposal protects scarce resources from GC timing.

### Maintainer load

Two targets require continuous conformance and tooling work. The IL self-host,
portable core, and target-neutral IR are independently useful checkpoints. The
native target begins only after those foundations prevent semantic duplication.

## Required design decisions

The following contracts must be settled before the portable core is frozen:

1. Integer overflow, conversion, shift, and division rules.
2. Float NaN, signed-zero, comparison, hashing, parsing, and formatting.
3. Decimal representation, scale, overflow, and rounding.
4. Unicode indexing, normalization, casing, comparison, and graphemes.
5. `nothing`, `null`, `T?`, `Option<T>`, and foreign-null conversion.
6. Collection mutability, identity, equality, hashing, and iteration order.
7. Named records, anonymous records, and dynamic `Row` behavior.
8. Function output streams, returns, iterators, futures, and channels.
9. Errors, stack traces, cancellation, `defer`, and cleanup failure.
10. Type descriptors, reflection retention, dispatch, and generic metadata.
11. Portable regex syntax and worst-case execution guarantees.
12. Temporal types, calendar arithmetic, timezone data, and serialization.
13. C ownership, pointer lifetime, callbacks, and ABI versioning.
14. CLR conversion, identity, task, exception, and collection-adapter rules.
15. Capability inference for unresolved dynamic code.

## Completion criteria

Tōast is self-hosting on .NET when:

- the compiler is written in Tōast;
- the existing compiler can build it;
- the produced compiler can compile its own source;
- successive IL stages reach a reproducible fixed point;
- no source replay or interpreter dependency exists in the compiler artifact;
- the portable conformance corpus passes.

Tōast is self-hosting natively when:

- the native runtime implements the versioned core contract;
- the self-hosted compiler emits C for its source and portable dependencies;
- the system C compiler produces a compiler requiring no .NET components;
- successive native stages reach an IR and generated-C fixed point;
- the native compiler passes the portable conformance corpus;
- `no_clr` verification proves no CLR capability enters the artifact;
- the runtime passes memory, sanitizer, startup, and ABI gates.

TōSh can then be rebuilt incrementally in Tōast against either target. The
language becomes self-hosting before every shell component and development tool
is rewritten.

---

## Review notes — 2026-08-17

An engineering review of this document, recorded here rather than in a commit message
so the questions travel with the design. Nothing below is a decision; each is either a
gap to close or a risk to name.

### The status line contradicts the content

The header says *"Exploratory design. Not scheduled."* but Phases A–C map onto plan
items that are live now — `TS-P3-15`–`TS-P3-20` are on the board and referenced by name
from the delivery sequence. Either this is exploratory and those should not be worked,
or it is the plan of record. It reads like the latter.

Related: **"Assumes Tōast and TōSh are finished, stable"** is a precondition that never
arrives. Software is not finished. Phase A already names real gates — specified core
semantics, a backend-neutral corpus — and those are a better precondition than a word
that in practice means *never start*.

### Bootstrap provenance is unspecified

The IL bootstrap begins `Existing C# compiler → Tōast compiler IL-0`. After IL-1
exists, what happens to the C# compiler? Every self-hosted language has to answer this:
Nim ships `csources`, Rust ships a stage0 binary, Go kept a C bootstrap until 1.4.

The question is not academic — it is *how does someone rebuild this from source in ten
years*. Options are roughly: keep the C# compiler maintained as a peer, freeze it as a
checked-in bootstrap artifact, or check in a generated C snapshot of the native
compiler. Each has different long-term costs, and choosing now is far cheaper than
choosing after the C# compiler has bit-rotted.

### The `Any` leak is the highest-risk part of the capability model

The capability section correctly identifies the hard case — *"portable modules that
expose CLR-backed values through `Any` or an apparently portable interface"* — but the
model is described as static verification over the typed IR, and a dynamic value's
origin is not always statically known.

If closing that hole requires runtime capability tagging on dynamic values, it has a
throughput cost and touches the core value model. That is a large enough consequence to
deserve its own section rather than a bullet, because the answer changes both the
performance story and Phase C's exit criteria.

### There is no stop criterion

For a multi-year programme this document names exits for every phase but no condition
under which a phase should be re-scoped or abandoned. If Phase C establishes that
capability enforcement costs 30% throughput, or Phase E finds conservative GC
unworkable with FFI callbacks across threads, what happens? Naming those in advance is
how a long programme avoids sunk-cost reasoning later.

### Smaller notes

- **Boehm GC** is conservative and non-moving. Behind an allocation interface it is a
  reasonable first choice, and §Native runtime verification already stresses *"roots
  across callbacks, threads, and suspension points"* — which is exactly where
  conservative collection plus FFI goes wrong. Worth stating explicitly that the
  interface exists so it can be replaced, not merely wrapped.
- **Runtime ABI stability across the bootstrap** is implied by `N-0 → N-1 → N-2` but
  only tested ("ABI compatibility across runtime versions"). The chain requires the ABI
  to be stable across compiler versions; that is a constraint worth stating where the
  bootstrap is defined.
- **`dotnet` + `no_clr` as a verification target is the strongest idea in the
  document.** It proves portable-source separation before any native code generation
  exists, using infrastructure that already exists. That deserves to be called out in
  the Summary rather than only appearing in the target table.

### How current work relates

`TOAST-0006` is already building the boundary this document's §Architectural layers
describes: `ICommandTable`, `IToastHostSignals`, `IToastDiagnosticSink` and
`ToastOptions` are early instances of the platform-and-interop seam, and `ToastRuntime`
is the runtime-kernel/portable-core split beginning. The two efforts converge rather
than compete, which is the main reason to trust the direction.

The one place they disagree is recorded and resolved: `TOAST_SEPARATION_PLAN.md` had
frozen the compiled backend, while this document puts the existing compiler on the
critical path. The freeze was reversed on 2026-08-17 and had never been executed.
