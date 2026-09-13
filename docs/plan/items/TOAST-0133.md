---
id: TOAST-0133
title: "The shadowing warning covers one declaration kind and one pair of names, so displacing `int` is silent"
status: proposed
area: toast
priority: 3
opened: 2026-09-12
---

## Problem

A type parameter may be named anything, including the name of a type that already
means something — and nothing says so:

```tosh
class Box<int>(v: int) { prop V: int = $v }
(new Box<string>("x")).V        # "x"
```

`int` inside that class means the type parameter, so a property annotated `int` holds
a string. The displacement is invisible: no warning, no note, nothing in the hover.

## The warning exists and almost never fires

`ToshEngine.WarnIfShadowingCoreType` emits `tosh.naming.shadowed_core_type`, and its
own remarks give exactly the right reason for it:

> The declaration wins … It is warned about rather than accepted silently because
> `Option` and `Result` are names a user may take without meaning to displace
> anything, and the displacement is otherwise invisible.

That argument applies word for word to `int`. But the warning is narrowed twice over:

**One call site.** It is reached from union declarations and nothing else
(`ToshEngine.cs:2201`). Measured against the installed binary:

| declaration | warns? |
|---|---|
| `union Option { A, B }` | **yes** |
| `class Option(v)` | no |
| `class Box<Option>(v)` | no |
| `class Box<int>(v)` | no |
| `var int = 5` | no |

**One pair of names.** `CorePrelude.TypeNames` is `{ "Option", "Result" }`, so the
built-in aliases — `int`, `string`, `double`, `bool` — are outside it entirely, even
though displacing one of those is the more surprising of the two: `Option` is a name a
user might reasonably take, while `int` is a name they almost certainly did not mean to
redefine.

## Not a correctness bug

The resolution rule is deliberate and documented: a declaration wins over a built-in,
the same way a user `func double` beats the `double` alias. Nothing here proposes
changing that. What is missing is the *notice* — the same notice the language already
decided this class of displacement deserves.

## Shape of a fix

Two independent halves, each small, and worth measuring before either is assumed:

- Reach the warning from the other declaration kinds — class, record, struct, enum,
  trait, interface, type alias, and a generic type-parameter list.
- Decide what set it guards. Adding the built-in aliases would be the substance of it;
  the risk is noise, since `func double` beating `double` is called out in the code as
  *intended* and presumably happens on purpose in real libraries. Worth counting the
  hits across ToastLib and the repository's examples before choosing.

## Acceptance

- [ ] `class Box<int>` warns, or a recorded decision says why it should not
- [ ] Whatever set is chosen, the same set is used from every declaration kind, so the
      answer does not depend on which keyword was written
- [ ] The count of real-world hits is measured first, so the noise cost is known rather
      than guessed
