---
id: TOAST-0085
title: "Type aliases and refinements remain interchangeable with their base, so domain values can be mixed accidentally"
status: proposed
area: toast
priority: 2
opened: 2026-08-28
---

## Problem

A structural alias gives a useful spelling and a refinement validates a predicate, but neither
creates nominal identity. Two identifiers represented by `int`, or two lengths represented by
`double`, still pass through the same APIs and operators. C#/F#/Rust-style wrappers prevent that
category of bug, but a wrapper class is too allocation-heavy and verbose for a shell language.

## Candidate surface

```tosh
distinct type UserId = int where _ > 0
distinct type OrderId = int where _ > 0

func load-user(id: UserId) { ... }

var user = UserId(42)
var order = OrderId(42)
load-user $user                         # accepted
load-user $order                        # type error despite the same representation
load-user 42                            # explicit construction required

echo $user.Value                        # explicit unwrap; exact spelling to be designed
```

A `distinct` type is zero-cost when its representation permits it, but its nominal identity must
survive generics, reflection metadata and overload resolution. Representation compatibility is
not conversion compatibility.

## Operators and protocols

Blindly inheriting every base operator recreates accidental mixing (`UserId + OrderId`). Blindly
inheriting none makes numeric measures unusable. The declaration must explicitly derive or expose
the protocols it wants, and binary operations must state their operand and result types. `Eq`,
`Hash` and rendering are reasonable derivable defaults; arithmetic is a design choice.

## Acceptance

- [ ] A nominal distinct-type declaration wraps an existing base type without heap allocation when
      the target representation allows it
- [ ] Construction validates any `where` predicate and reports the distinct type on failure
- [ ] The base type, and another distinct type over the same base, are not implicitly assignable
- [ ] Wrapping and unwrapping are explicit and have one canonical spelling
- [ ] Operator/protocol derivation is explicit enough that mixed-domain arithmetic cannot reappear
- [ ] Equality, hashing, dictionary keys, pattern matching and rendering preserve nominal identity
- [ ] Generic inference and constraints bind the distinct type, not its underlying representation
- [ ] CLR/native ABI transparency is opt-in and documented separately from language conversion
- [ ] `typeof`, describe-type, help, LSP and emitted metadata expose both identity and representation
- [ ] Interpreter, compiler and differential fixtures cover two same-representation types

## Relationship to existing types

Ordinary `type Name = Base` aliases keep their transparent spelling role. Refinements keep their
validated-subset role. `distinct type` composes with a refinement when nominal identity is the
point. `TOAST-0055` must reject an unknown constraint rather than letting a misspelled protocol
silently weaken a distinct type.
