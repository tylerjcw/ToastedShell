---
id: TOAST-0016
title: "`extend` matches only CLR type names, so `extend int` silently never applies"
status: open
area: toast
priority: 2
opened: 2026-08-17
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

- [ ] `extend int` reaches `System.Int32`, and the same for every alias the annotation
      position accepts (`string`, `bool`, `float`, `file`, …)
- [ ] `extend Int32` and `extend System.Int32` keep working, pinned as controls
- [ ] resolution goes through the one alias table, not a second list of pairs
- [ ] an `extend` naming a type that resolves to nothing is **reported at declaration**
      rather than going quiet — that is the part that made this cost an afternoon
- [ ] a negative control: reverting fails the new tests

## Notes

Found while checking the precedence order for `TOAST-0001`: an "extension beats a free
function" probe returned the function, which looked like a bug in that fix and was not.
The extension had simply never registered.

Worth deciding at the same time whether the extension key should be a resolved `Type`
rather than a string. Matching on names is what allows `extend Point` to reach both a
ToastScript class and a CLR type, so the string is doing real work — but it is also why
a name that resolves to nothing looks identical to one that has not been used yet.
