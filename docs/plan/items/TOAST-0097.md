---
id: TOAST-0097
title: "A type cannot be given a static member from outside, so `Option::from` has nowhere to live"
status: complete
area: toast
priority: 3
opened: 2026-08-29
---

## Problem

`extend` adds *instance* methods and nothing else. A `static func` inside an `extend` block
parses and is silently never found:

```tosh
class C { prop X = 1 }
extend C { static func make() { return "hi" } }

C::make()        # tosh.runtime.expression_failed — no such static
```

`static func` in a **class body** works, so this is specific to `extend`. And a union has no body
that takes methods at all — `union M { A(v) func f() { } }` is a parse error — so for a union
there is no route to a static member from anywhere.

The failure mode is the one `TOAST-0016` already fixed once for instance extensions: a
declaration that is accepted, stored, and then matches nothing at the point of *use*, in a place
that looks unrelated to the declaration.

## Why it came up

`TOAST-0083` decided that `null` and `Option<T>` convert only by name, and the surface put to the
user for that decision read:

```tosh
var v: Option<string> = Option::from(Env::get("HOME"))
```

`Option::from` cannot exist. `Option` is a union, so it has no body for a static, and `extend`
cannot supply one. The conversion shipped as a free function instead:

```tosh
option-from $nullable        # what exists
Option::from($nullable)      # what was described
```

`or-null()` is unaffected — it is an instance method and lives in `extend Option` as intended.

## Candidate surface

```tosh
extend Option {
    static func from(value) {
        return ($value is null ? Option::None<dynamic>() : Option::Some($value))
    }
}
```

`_extensionMethods` is keyed by receiver type name and consulted only when there *is* a receiver.
A static extension needs the same table consulted from the static-member resolution path, where
the "receiver" is the type itself.

## Acceptance

- [x] `static func` in an `extend` block is reachable as `Type::name(…)` and `Type.name(…)`
- [x] It works for a union, whose own body cannot declare one at all
- [x] A `static func` that is accepted must be findable — no silent registration, per `TOAST-0016`
- [x] `option-from` becomes `Option::from`, with the free function **kept**, deliberately —
      it is a bareword, which is what a pipeline wants, and it is documented and used
- [x] Extending a type that already declares a static of that name is a diagnostic, not a
      silent winner
- [x] Interpreter and compiler agree — **deferred.** Compiled tosh is an experiment until
      the interpreted language is solid

## Fix — 2026-09-20

Worse than filed. The modifier was not unsupported, it was **discarded**: a `static func`
was written into the instance table with the rest, so `Type::name()` found nothing while
`$value.name()` answered it. The declaration was not merely unreachable — it answered the
wrong call.

`_extensionStatics` is a second table keyed exactly as `_extensionMethods` is, sharing one
dictionary across every name a type answers to, so `extend int` and `extend Int32` still add
to one place. The two are separate because they are reached from different points: an
instance method needs a receiver, a static needs only the type.

The static path consults it *before* ordinary resolution, which is the opposite of the
instance path and is deliberate. `CanPlanQualifiedInvocation` answers whether the **type**
resolves, not whether it has the member, so ordering it first left `string::Shout` planning
successfully against `System.String` and then finding nothing — the extension was skipped for
a member that does not exist. Going first is safe because a name that would displace a
declared static is refused at its declaration:

```
'K' already declares a static 'make'.
  help: 'K.make' wins, because an extension is consulted only after the type's own members
        decline. Rename the extension, or change the type itself.
```

That check covers a declared class and a CLR type alike, so `extend string { static func
Join() }` is refused against `System.String.Join`. A real static still answers: `string::Join`
is unchanged with the extension table populated.

`Option::from` now exists, which is what the item was for. `TOAST-0083` described that
surface and the prelude had to ship `option-from` instead, its comment recording exactly why
— "a union body takes variants only and `extend` adds instance methods, so there is no place
to hang a static on a union today". That is no longer true. Both spellings are kept and the
comment says why: a bareword is what a pipeline wants, and `option-from` is documented in the
command reference and used.

`ExtensionStaticTests` covers both spellings, the union case, the option conversion through
both names, the collision against a declared and a CLR static, a real static still winning,
and that instance extensions are untouched.

## Closed — 2026-09-20

The interpreted work is done. This stayed open only on its final criterion, that the
interpreter and the compiled backend agree — which they do not, by decision: compiled
ToastScript is an experiment until the interpreted language is solid
(`docs/ROADMAP.md`, *Standing Priority Decision*), so no new surface is added there.

Indexed in [`TOAST-0135`](TOAST-0135.md) with the others, so the question has one live
answer rather than a finished item held open to hold it.
