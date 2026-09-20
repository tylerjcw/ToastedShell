---
id: TOAST-0136
title: "A required module's functions are callable unqualified in an expression, but nowhere else"
status: open
area: toast
priority: 2
opened: 2026-09-20
---

## Why this exists

Requiring a file that exports a module makes that module's exported functions callable
*unqualified* — but only from expression position, and only when the module arrived through
`require`. The same function is not a command, and the same module declared in the same file
is not reachable this way at all.

Found while building `require X as A` ([`TOAST-0134`](TOAST-0134.md) notwithstanding — this
is older than that work and reproduces on the installed binary with the doubled form). A
test asserting the opposite was written and failed; the assertion was wrong, not the engine.
This item is that measurement, filed rather than absorbed.

## The measurement

`Thing.tosh`, required by path so nothing about the library index is involved:

```tosh
export partial module Deep.Thing
export func Speak() { return "modular" }
export func Other() { return "other" }
```

| Caller | `Speak` reaches | |
|---|---|---|
| `require Deep.Thing from "./Thing.tosh" as T` then `(Speak())` | **yes** | "modular" |
| `require Deep.Thing from "./Thing.tosh" as T` then `Speak` | no | `tosh.runtime.unknown_command` |
| `require Deep from "./Thing.tosh" as D` then `(Speak())` | **yes** | binding the *outer* module is enough |
| `require "./Thing.tosh"` then `(Speak())` | **yes** | no alias involved either |
| `require Deep.Thing.Other from "./Thing.tosh"` then `(Speak())` | no | importing one member imports one member |
| `module M { export func Speak() … }` in the same file, then `(Speak())` | no | `tosh.runtime.unknown_command` |
| require inside `if (true) { … }`, then `(Speak())` outside | no | the reach is scoped, not global |
| `func Speak() …` declared locally, then require, then `(Speak())` | local wins | "local" |
| `(Nope())` | no | still a clean error — this is a lookup, not a catch-all |

So the reach is real, scoped and shadowable — it behaves like a binding rather than a bug in
the error path — and it is inconsistent along two axes at once:

- **Call form.** `Speak` and `(Speak())` are the same call written two ways, and they
  disagree. Command position says the name is unknown; expression position calls it.
- **Origin.** A module reached through `require` grants it; the identical module declared
  in place does not.

## What would close this

A decision on which behaviour is intended, then both axes made to agree:

- [ ] Decide: is a required module's export reachable unqualified at all?
- [ ] If yes — command position resolves it too, and a locally declared module grants the
      same reach. `Speak` and `(Speak())` mean one thing.
- [ ] If no — expression position stops resolving it, and the spelling is `T.Speak()`.
      Check the reader's library and `examples/` first; anything relying on the short form
      has to be found before it breaks.
- [ ] Whichever way it goes, the specification says so. It currently says neither.
- [ ] A conformance test per row of the table above, so the two positions cannot drift apart
      again.

## Notes

Not a regression, and not introduced by the `as` alias: every row reproduces on the
installed binary using the doubled `require X from X as A` form that predates it. The alias
work simply put a module in scope often enough to notice.
