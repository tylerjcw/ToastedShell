---
id: TOAST-0016
title: "`extend` matches only CLR type names, so `extend int` silently never applies"
status: complete
area: toast
priority: 2
opened: 2026-08-17
closed: 2026-08-20
---

## Problem

An `extend` declaration is stored under the type name as written, and looked up against
the names `EnumerateReceiverTypeNames` produces for the receiver:

```csharp
if (receiver is IShellTypedObject typed)
{
    yield return typed.ShellTypeDescriptor.ShellTypeName;
    yield return typed.ShellTypeDescriptor.ShellFullName;
}

var clr = receiver.GetType();
yield return clr.Name;                 // Int32
if (clr.FullName is { } full) yield return full;   // System.Int32
```

A CLR receiver such as `1` is not an `IShellTypedObject`, so the only names offered are
`Int32` and `System.Int32`. The shell alias is never among them:

```tosh
extend int { func tag() -> string => "ext" }
(1).tag()      # error: no overload matched instance method 'tag' on 'System.Int32'

extend Int32 { func tag() -> string => "ext" }
(1).tag()      # "ext"
```

**It fails silently at declaration.** `extend int { ... }` is accepted, registered, and
then never matches anything. Nothing reports that the extension is unreachable; the only
symptom is the eventual "no such method" at a call site that looks unrelated.

`int` is the spelling the language uses everywhere else — `var n: int`, `func f(x: int)`,
`cast int` — so it is the one an author will reach for first, and the alias table already
exists to resolve it.

## Acceptance

- [x] `extend int` reaches `System.Int32`, and the same for `string`, `bool`, `double` and
      `float`
- [x] `extend Int32` keeps working, pinned as a control
- [x] resolution goes through the one alias table — `ResolveTypeName`, the same resolver an
      annotation uses, not a second list of pairs
- [x] ~~an `extend` naming a type that resolves to nothing is reported at declaration~~ —
      **withdrawn, not outstanding.** A forward reference is legal, so at the moment an
      `extend` runs there is no way to tell an unresolvable name from one declared three
      lines later. Recorded 2026-08-17 and unchanged since; the item is closed on the
      four boxes that were met and this one being retired rather than met —
      **not done, and not doable as written.** See below.
- [x] a negative control: 4 of 10 fail with the registration reverted

## Resolution — 2026-08-17

An extension is registered under **every name its receiver can answer to**, not just the one
that was written. `ResolveTypeName` — the same resolver an annotation uses — turns the
written name into a CLR type when it names one, and the type's `Name` and `FullName` join
the written name as keys.

An alias and its CLR name are the *same type*, so `extend int` and `extend Int32` add to one
table rather than two that shadow each other; a test pins that. A name that resolves to no
CLR type keeps only its written key, which is what lets `extend Point` reach a declared
ToastScript class.

### Why the declaration-time report cannot be written as specified

**A forward reference is legal and useful:**

```tosh
extend ExtLater { func twice() -> int => ($this.N * 2) }
class ExtLater { prop N: int = 3 }
```

At the moment an `extend` is evaluated, a name that resolves to nothing is
indistinguishable from one whose type is declared three lines later. Reporting there would
break the spelling above, and a deferred end-of-script check is a different piece of
machinery than this item is scoped to.

The realistic failure mode is gone regardless: what cost an afternoon was `extend int`
being accepted and silently never matching, and that is fixed. A typo'd `extend Nonexistent`
is a rarer case and still reports only at the call site.

### Noticed, not fixed

`extend System.Int32` does not parse — the statement is not recognised and `extend` is
reported as an unknown command. That is the `extend` grammar's own limitation, unrelated to
which names it registers under, and out of scope here.

## Notes

Found while checking the precedence order for `TOAST-0001`: an "extension beats a free
function" probe returned the function, which looked like a bug in that fix and was not.
The extension had simply never registered.

Worth deciding at the same time whether the extension key should be a resolved `Type`
rather than a string. Matching on names is what allows `extend Point` to reach both a
ToastScript class and a CLR type, so the string is doing real work — but it is also why
a name that resolves to nothing looks identical to one that has not been used yet.
