# TōSh Unit-System Stabilization

Status: active design and implementation work (`TS-P3-07`)

This document is the source of truth for making physical quantities a first-class
ToastScript feature. It records the language contract, the defects found during
dogfooding, and the order in which they will be corrected. The stabilization log
continues to record dated evidence; this document owns the unit-system design.

## Goals

- Make quantity values reliable in literals, expressions, variables, functions,
  classes, script arguments, interpreted execution, and compiled execution.
- Make conversion explicit and readable: ``distance as `ft``,
  ``duration as `s``, and ``speed as `mph``; retain `.To("unit")` for
  programmatic use.
- Accept the conventional degree sign without a backtick:
  `90°`, `20°C`, `68°F`, and `540°R`.
- Preserve dimensional correctness through compound arithmetic.
- Give data sizes and durations one understandable language-facing model while
  retaining compatibility with existing shell values.
- Validate the design with real scripts, beginning with
  `examples/reactor-block.tosh`.

## Language contract

### Literals

The general spelling remains ``number`unit``. It is explicit, works for every
registered simple or compound unit, and avoids collisions with ordinary command
arguments.

U+00B0 DEGREE SIGN is the one adjacency shorthand:

- `90°` is an angle and is equivalent to ``90`deg``.
- `20°C`, `68°F`, and `540°R` are absolute temperatures.
- `°K` is invalid; Kelvin is written `K` and has no degree sign.
- Bare `C` and `F` keep their electrical meanings (coulomb and farad).
- Lookalikes such as `º`, `˚`, and `∘` are not silently normalized to `°`.

The shorthand is intentionally narrow. TōSh will not make every adjacent
number-and-unit pair a source literal: `5km` remains ordinary shell text, while
external script arguments may be converted from the string `5km` when their
declared parameter type is a quantity.

Numeric separators obey the same rules in every numeric and quantity literal.
An underscore must be between two digits; `1_000`, ``1_000`m``, and `1_000°`
are valid, while ``1__0`m``, ``1_`m``, and ``_1`m`` are diagnostics.

### Representation and conversion

A quantity has four independent facts:

1. its displayed magnitude;
2. its displayed unit symbol;
3. its physical dimension;
4. the transform between that display unit and the dimension's base value.

The transform is part of the value. It must not be reconstructed only from a
simple registry key, because compound units such as `km/hr` have a real scale
but no single registered symbol. This invariant fixes the current corruption in
which ``1`km/hr`` has a base value of `1` instead of `0.277777...`.

The idiomatic ToastScript conversion is ``quantity as `target``:
``2`mi as `ft``, ``2`hr as `s``, or ``10`m/s as `mph``. The leading backtick
distinguishes a unit target from the existing type conversion `value as Type`.
`quantity.To("target")` and `quantity.ConvertTo("target")` are equivalent
programmatic/CLR-friendly forms. A target may be simple or compound and must
have the same dimension.

Normal display and string interpolation treat a quantity as a scalar and render
its selected magnitude and unit, such as `483.06 MW`. Quantities remain
record-like for explicit member access and pipeline introspection; that
structured surface must not cause ordinary interpolation to dump `.value`,
`.dimension`, or `.base-value` fields. Default quantity text uses fifteen
significant digits to suppress ordinary binary floating-point noise; callers
that require round-trip text can request the explicit `R` numeric format. The
default external shell-text spelling follows this human-readable display policy;
machine serialization should request `R` explicitly.

Addition and subtraction preserve the left operand's display unit.
Multiplication and division operate on base values and return a canonical,
base-unit presentation for the resulting dimension. A dimensionless quotient is
a numeric scalar in ToastScript, not a quantity wearing an empty unit.

### Temperature points and differences

Celsius and Fahrenheit are affine scales, so absolute temperature values are
points, not freely scalable vectors. The final model will distinguish an
absolute temperature from a temperature difference:

- point minus point produces a temperature difference;
- point plus point is invalid;
- point plus or minus difference produces a point;
- multiplying or dividing a point by a scalar is invalid;
- differences convert by scale only, without an absolute offset.

Until the difference type and spellings are implemented, conversions of
absolute temperatures are supported but ambiguous affine arithmetic must not be
presented as settled behavior. Candidate difference spellings are `deltaC`,
`deltaF`, `Δ°C`, and `Δ°F`; this remains an explicit follow-up decision.

### Data sizes

`DataSize` is the language-facing quantity category. Unit symbols are
case-sensitive: `b`/`bit` are bits and `B` is a byte; decimal and binary prefixes
retain their usual distinction (`MB`, `Mb`, `MiB`). The internal dimensional
base remains the bit for compatibility with the existing registry.

`StorageSize` remains the existing shell/CLR bridge for integral byte counts and
for legacy suffix literals such as `10mb`. Conversion between the two is
explicit in the runtime conversion layer. A `DataSize` that is not an integral
number of bytes cannot be converted losslessly to `StorageSize`.

Longer term, storage suffix literals should become syntax sugar for `DataSize`.
That migration requires a compatibility plan because the suffix parser is
case-insensitive and historically interprets `kb` as kilobytes, while physical
unit notation interprets `kb` as kilobits.

### Time values

`DurationQuantity` models a fixed physical duration and converts through seconds.
`TimeSpan` is its CLR bridge. `TemporalAmount` remains the calendar-aware value
that can contain months and years.

The existing `duration` type alias continues to mean `TemporalAmount` during
stabilization. Physical-duration annotations use `DurationQuantity` or the new
unambiguous alias `timequantity`. Renaming `duration` would be a compatibility
change and must be handled separately.

### Type categories

Dimensions alone do not uniquely determine semantic categories: energy and
torque share a dimension. Named CLR subclasses are therefore compatibility and
annotation conveniences, not the long-term source of truth. The durable model is
one `Quantity` carrying dimension plus semantic kind metadata. Adding that kind
metadata is later work; until then, parsing a named unit should preserve its
registry category wherever possible, and derived values use the dimension's
canonical category.

## Implementation stages

### Stage A — implemented; focused validation pending

- Preserve simple and compound conversion transforms on every quantity.
- Reject affine units inside compound expressions until difference units exist.
- Canonicalize multiply/divide results instead of concatenating display labels.
- Add idiomatic ``quantity as `target`` conversion, its
  `Quantity.To(string)` / `ConvertTo(string)` API equivalents, and string parsing
  for typed script boundaries.
- Return scalars for dimensionless division in ToastScript.
- Add focused runtime and language regressions.

### Stage B — implemented; focused validation pending

- Register standalone `°` as an angle unit.
- Lex adjacent `°`, `°C`, `°F`, and `°R` forms as quantity literals.
- Share numeric-prefix validation with backtick quantity literals.
- Give invalid explicit units a structured unit-literal diagnostic rather than
  allowing them to fall through as commands.
- Update source highlighters and the specification from their generators.

### Stage C — implemented; focused validation pending

- Add friendly quantity annotation aliases without taking over the existing
  `duration` alias.
- Convert external strings such as `5km`, `2hr`, and `10m/s` at typed script and
  function boundaries.
- Complete `DataSize`/`StorageSize` and `DurationQuantity`/`TimeSpan` bridges.
- Support quantities in `sum`, `average`, `min`, and `max`.
- Emit quantity literals and resolve quantity annotations in compiled scripts.
- Route `is`, `is-not`, `as`, and `cast` through the same type resolver.

### Stage D — dogfooding started; semantic work remains

- Make `reactor-block.tosh` deterministic from its `ReactorType` argument; keep
  any interactive picker in a separate interactive entry point.
- Replace `.value` escape hatches with dimensionless scalar results and assert
  the uranium 2x2 reference calculation.
- Introduce temperature-difference semantics and spellings.
- Add quantity kind metadata so torque/energy and similar equal-dimension
  categories remain distinguishable.
- Make unit registries engine-scoped or otherwise concurrency-safe; parsing must
  not mutate a process-global dictionary during reads.
- Decide targeted Unicode aliases (`µ`/`μ`, `Ω`/`Ω`, `℃`/`°C`, `℉`/`°F`) without
  compatibility-normalizing identifiers or whole source files.

The deterministic reactor fixture, dimensionless count ratios, pure prefix
lookup, immutable dimension views, and registry collision protection are now in
place. The remaining Stage D architecture is deliberately not disguised as
finished: existing interpreted values retain their resolved conversion through
arithmetic, but compiled literals still serialize magnitude plus symbol and
resolve that symbol when the assembly runs. Engine-scoped unit overlays and an
immutable resolved-unit payload (including semantic kind and point/delta role)
must close that last time-of-use gap.

## Remaining follow-ups

- Define temperature-difference units and point/difference arithmetic before
  enabling temperature addition, subtraction, scaling, or compound point units.
- Carry semantic kind independently of dimensions so energy/torque and other
  equal-dimension concepts remain stable through derived arithmetic.
- Serialize a resolved unit payload in compiled assemblies instead of looking up
  a display symbol at execution time.
- Make custom-unit registration engine-scoped. The current singleton now uses
  synchronized, non-mutating reads and refuses built-in replacement, but custom
  definitions still have process-wide lifetime.
- Add unit-aware integer exponentiation (`quantity ** integer`), followed by
  deliberately specified floor-division, remainder, roots, and approximate
  equality. `Numeric` remains scalar-only; quantity operator traits are exposed
  separately because dimensional arithmetic is not type-closed.
- Add recovery tokens for incomplete/invalid quantity literals so one typing
  error does not suppress all lex-driven CLI, Tome, and LSP highlighting.
- Decide targeted Unicode symbol aliases with explicit diagnostics; never apply
  compatibility normalization to identifiers or the whole source file.

The diagnostic references and generated TextMate grammar were regenerated from
their sources on 2026-08-12. Command metadata, the specification PDF, and a new
VSIX require a current build and remain intentionally pending while build/test
execution is paused. The pre-existing `26.8.13` VSIX is stale and must not be
used as evidence for this work.

## Validation policy

Use narrow unit, lexer, conversion, compiler-emitter, and script-integration
selections. Do not run the full solution test suite: the stabilization log's
full-test note documents its memory multiplier and the required constrained
invocation. During the current memory investigation, no test or build command is
run unless it is explicitly resumed.
