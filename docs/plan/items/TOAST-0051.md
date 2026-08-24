---
id: TOAST-0051
title: "Operator dispatch has no CLR `op_*` fallback, so a `Vector3` cannot be added to a `Vector3`"
status: complete
area: toast
priority: 1
opened: 2026-08-22
---

## Problem

`System.Numerics.Vector3` is reachable today. It constructs, its members read, and its
static methods call:

```tosh
using System.Numerics
var a = new Vector3(1.0, 2.0, 3.0)
$a.X                                    # Single 1
Vector3.Dot($a, $a)                     # Single 14
```

What it cannot do is arithmetic:

```tosh
$a + $a
# tosh.runtime.expression_failed
# Operator operands 'System.Numerics.Vector3' and 'System.Numerics.Vector3'
# are not compatible.
```

This is not a general failure of operator evaluation. Measured, not assumed:

| Expression | Result |
|---|---|
| `BigInteger + BigInteger` | `200` — works |
| `TimeSpan + TimeSpan` | `8 seconds` — works |
| `Vector3 + Vector3` | `expression_failed` |

So the evaluator consults a set of types it already knows and never asks the type itself.

## Where it already knows how to ask

`src/Tosh.Runtime/OperatorEvaluator.cs:1946`:

```csharp
var n when string.Equals(n, "Add", StringComparison.OrdinalIgnoreCase)
    => HasCompatibleOperator(type, "op_Addition"),
```

That is the *generic-constraint* path — answering "does `T` satisfy `Add`". The arithmetic
path never calls it. `GetMethod("op_` appears nowhere in the file.

So the mechanism exists, is correct, and is wired to the wrong question.

## Why this is priority 1 rather than a convenience

It is one fix in one shared file, and `OperatorEvaluator` is called from both
`ToshEngine.Operators.cs` and `BoundUnitEmitter.Expressions.cs` — so it lands once and both
backends get it, which is rare enough in this codebase to be worth spending first.

What it unblocks is not one type. It is every CLR type with operators that anyone loads:
`Quaternion`, `Matrix4x4`, `Complex` from the BCL rather than the shell's own, and every
`struct` returned from a bound native library. Any Tōast program doing vector maths,
physics, or graphics is currently writing `$v.add($w)`.

It is also the precondition for blessing `Vector2/3/4` and friends as aliases. Aliases
without this are sugar over a mechanism that does not work.

## Not the same as `TS-P3-03`

`TS-P3-03` (reverse/static operator hooks) is about the *right* operand of a noncommutative
mixed-type operation. This is about the operator not being found at all. Related subsystem,
different defect; `TS-P3-03` presumes lookup already succeeds.

## Acceptance

- [x] Binary arithmetic and comparison fall back to the operand type's `op_*` static
      method when no built-in rule matches
- [x] `$a + $b` works for `Vector3`, `Quaternion`, and `Matrix4x4` without a `using`-specific
      special case
- [x] The fallback reuses `HasCompatibleOperator`'s lookup rather than adding a second one —
      one description of "does this type have this operator"
- [x] A `struct` from a `bind native` library with `op_Addition` participates
- [x] Interpreted and compiled agree, in the differential corpus
- [x] The failure message for a type with genuinely no operator still names both operand
      types, as it does now
- [x] `§Operators` records that CLR operator methods are consulted, and where in the
      resolution order

## Resolution — 2026-08-24

`OperatorEvaluator` now has one `FindCompatibleOperator` lookup. The existing `Add`/`Sub`/
`Mul`/`Div` trait checks ask it whether an exact same-type operator exists, while runtime
evaluation asks it for the actual operand pair and invokes the returned method. Built-in
arithmetic, equality, collection, enum, conversion, and ordering rules remain ahead of the
fallback; the left CLR type is consulted before a distinct right type.

Arithmetic maps to the conventional `op_Addition`, `op_Subtraction`, `op_Multiply`,
`op_Division`, and `op_Modulus` names. Equality consults CLR operators only after the
language's structural, exact-numeric, and conversion rules, and explicitly preserves
TōSh's reflexive `NaN` equality. Ordered comparisons reach the four CLR ordering names only
after enum, string, conversion, and `IComparable` handling. Reflection invocation unwraps
the operator body's exception so a `TargetInvocationException` never leaks into the
language.

Five direct runtime tests cover the three `System.Numerics` types, all comparison shapes, a
native-layout struct, the unchanged missing-operator diagnostic, and exception identity.
Three `System.Numerics` programs also run through the differential corpus. The focused CLR
and differential selection passes 154 tests; the full suite passes 6,557 with the existing
language-surface negative probe skipped.
