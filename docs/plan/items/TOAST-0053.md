---
id: TOAST-0053
title: "`match` cannot bind a union's fields, so dispatch is a switch on a string"
status: partial   # 10 of 11 accepted; the last waits on the compiler decision
area: toast
priority: 1
opened: 2026-08-22
---

## Problem

`§Match Expressions` lists five pattern forms: a literal, `_ <op> <value>`, `_ is <type>`,
bare `_`, and `default`. Every one of them tests the matched value as a whole. None binds
anything out of it.

So the central operation of writing a compiler cannot be written:

```tosh
match ($node) {
    Add(l, r)     => (eval $l) + (eval $r)
    Lit(v)        => $v
}
# error while evaluating this expression
```

What the specification offers instead is the form in `§Union Definitions`:

```tosh
switch ($r.Variant) {
    case "Ok"    { echo $r.value }
    case "Error" { echo $r.message }
}
```

A string comparison, then unchecked member access. A typo in `"Ok"` is a silent miss; a
typo in `.value` is a runtime error. Neither is reported where it is written.

## What is missing

Beyond variant patterns, every pattern form that binds:

```tosh
match ($node) {
    Add(l, r) if ($l is Lit)  => ...     # variant, binding, guard
    Node { kind: "if", body } => ...     # field patterns, shorthand bind
    [first, ..rest]           => ...     # list patterns with rest
    (Lit(0) | Lit(1)) as lit  => ...     # or-patterns, @-binding
    Some(Point(x, y))         => ...     # nesting
}
```

Guards already exist and work — `if ((<condition>))` after the pattern — so the arm
structure is in place. What is absent is anything on the left of the arrow that introduces
a name.

## Why this is the largest of the three

`TOAST-0052` gives variants types; this makes them reachable; `TOAST-0054` makes the set
complete. Of the three this is the one with the most grammar and binding work, and it is
the one whose absence is most visible: a compiler written without it is written in the
`switch`-on-string form above, a hundred thousand lines of it.

The parser work in `TS-P2-11` and `TOAST-0002` is adjacent — patterns are a
new expression-position grammar, and filing them into a parser that is being restructured
by hand is the more expensive order.

## First slice — 2026-08-28

Positional variant patterns, interpreted. `Ok(v)`, `Add(l, r)`, `Add(_, r)` and `Lit()` all
bind and dispatch; a guard sees what the pattern bound; a binding is scoped to its arm.

**The parser recognises the form only when the paren abuts the name.** `Ok (v)` with a space
is a command and its argument and stays that way, so adding this took nothing away from an
existing arm. Anything other than "bareword, paren, plain names, paren" — a call with an
expression argument, a literal, a nested pattern — is left to `ParseArgument`.

**Binding reads the variant's declared field names, not `GetMembers()`.** The first
implementation used the latter, which prepends a `Variant` entry, so `Ok(v)` bound `v` to the
string `"Ok"` and every pattern sat one position out — while still *matching*, so nothing
failed loudly. `Add(l, r)` on `Add(3, 4)` returned `"Add3"` rather than `7`. A test now names
that specifically, and the negative control which restores the old lookup fails six of the ten.

Arity is checked where the pattern is matched rather than where it binds, so a pattern naming
three fields of a two-field variant does not match and then bind null.

## The compiled backend does not have this yet

`--compile` refuses the shape by name — *"dynamic argument expressions (VariantPatternSyntax)
are not yet emitted"* — under every profile, so the differential corpus cannot carry a case:
its harness requires a clean emit, and this is not one. Refusing by name is the right failure,
but it means acceptance is **interpreted-only** for this slice and the corpus box stays open.

Emission needs its own bound node, lowering, and somewhere for the bound names to live as
locals the arm body can read — the same scope-pushing shape rune expansion needed. That is a
slice of its own rather than a detail of this one.

## Second slice — 2026-08-28

Patterns became *recursive*: a sub-pattern is an `ArgumentSyntax`, the same type the outer
pattern is built from, so nesting fell out rather than being added. `Some(Add(Lit(a), Lit(b)))`
binds two levels down, and named and positional forms mix freely — `Some(Lit { v })`.

Field patterns by name arrived with the same change. `Lit { v }` is shorthand for `Lit { v: v }`;
`Lit { v: got }` renames; and because the right of the colon is a *pattern* rather than a name,
`Node { kind: "if", body }` tests one field and binds another in one arm.

**The paren must abut but the brace need not.** `Ok (v)` stays a command and its argument, which
is what kept the first slice from taking anything away. `Node { … }` has no such collision — a
bareword followed by a block is not something a pattern could already have been — so requiring
the brace to abut only rejected the spelling everyone writes.

### A `$variable` sub-pattern matched anything

The lexer hands a variable reference back as a `Bareword` token whose *text* carries the `$`.
`ParseSubPattern` asked only for the token kind, so `$x` took the "a plain name binds" path:
`Lit($x)` bound a fresh `x` and matched every `Lit`, and `Node { kind: $expected }` matched every
node. Silently — rebinding a name that already exists is legal, so nothing failed.

`Lit(5)` and `Lit((2 + 3))` compared correctly the whole time, which is exactly what hid it. Only
the *miss* case can catch this, and the first test written for the form asserted a hit. Both
directions are now asserted, and the negative control which drops the guard fails those two tests
and nothing else.

Three sites needed the same guard — the pattern's own name, a field name, and a sub-pattern — so
the predicate is named `IsVariableToken` rather than repeated.

### Capture analysis had to learn the node

`SyntaxTraversalExhaustivenessTests` failed the moment `VariantPatternSyntax` existed:
`VariableBinder` did not walk it, so a reference inside a pattern was invisible to capture
analysis. Walking the sub-patterns was the fix rather than recording a gap, because
`Node { kind: $expected }` inside a closure is the case that needs the captured value — it is
pinned by a test that builds a matcher from a captured string.

### The unknown-field diagnostic is raised at runtime, not at binding time

Naming a field the variant does not have is now an error that names the field and suggests the
nearest declared one, instead of a silent miss. But it fires when the arm is *reached*, not when
the pattern is bound, so a bad pattern in an arm that never runs is still not reported.

Binding time is where it belongs, and it is not reachable yet: a `union` is an ordinary statement
evaluated at runtime, so at lowering there is no variant declaration to check the field against.
That wants union definitions visible to the binder, which is `TOAST-0052`'s territory rather than
a detail of this item. The box stays partial for that reason.

## What is left

Compiled emission with the differential corpus — the last box, and deliberately not next.

`--compile` refuses every pattern node by name, verified for all four: the right failure, but
it means the corpus cannot carry a case, since its harness requires a clean emit. Ordinary
patterns still compile, so the compiler guards are unaffected by any of this work.

Emission would need a bound node, lowering, and somewhere for the bound names to live as locals
the arm body can read — the same scope-pushing shape rune expansion needed. That is a second
implementation of everything above, and the standing decision is that compiled tosh stays an
experiment until the interpreted language is solid. This box waits for that.

## Third slice — 2026-08-28

Records, structs and classes destructure with the same grammar. Nothing about the pattern
form was variant-shaped: a pattern asks a value for its type name, its fields in order, and
its fields by name. Four kinds of value answer those, so they answer through one
`PatternSubject` rather than the matcher switching on the instance type in four places.

`new Point(3, 4)` matches `Point(x, y)` and `Point { x: 3, y }`; a `struct` binds positionally
from its declared fields; and the forms mix — `Some(Point(a, b))` reaches through a union
variant into a record.

**A class cannot be destructured positionally, on purpose.** Its properties may be inherited,
reordered or added without changing what the class means, so there is no order a positional
pattern could rely on — binding against one would run correctly until somebody added a
property to a base class. `Circle(r)` is refused with the named spelling in the help:
*name the fields — `Circle { Radius }`*. Named patterns do reach inherited properties, by
walking `BaseClass`.

The two diagnostics lost their `variant_` prefix — `tosh.runtime.pattern_arity` and
`tosh.runtime.pattern_unknown_field` — since they now fire for four kinds of value. Nothing
referenced the old codes; they were a day old.

`VariantPatternSyntax` keeps its name for now, which is no longer quite what it means. Renaming
it touches the parser, binder, matcher, binding walk and the exhaustiveness test, and is
mechanical enough to be its own change rather than noise inside this one.

## Fourth slice — 2026-08-28

List patterns. `[a, b]` matches a sequence of exactly that length, `[first, ...rest]` binds a
head and whatever follows, and the rest may sit in the middle — `[a, ...mid, d]` names both
ends. Elements are ordinary sub-patterns, so they test as well as bind, and list and variant
patterns nest into each other in both directions: `[Lit(a), Lit(b)]` and `Many([f, ...r])`.

**The rest is `...`, not the `..` this item sketched.** `..` is the range operator, so `[a, ..b]`
would have needed lookahead to tell from a range, and would read as one to anybody who knows
the rest of the language. `...` is the spread the language already has, in the one place where
"and the remainder" is what it means.

**A rest binds an array, not a list.** `[1, 2, 3]` is an `Int32[]`, so binding a `List` would
answer `.Count` where the literal it came from answers `.Length` — the same value needing a
different spelling depending on where it came from. A rest can therefore be matched again,
which is what makes walking a sequence possible.

**A string is not a list.** .NET makes it an `IEnumerable<char>`, so without an explicit refusal
`[a, b]` would match `"hi"` and bind two characters — a string quietly taking an arm written
for a list.

Two rests are refused at parse time rather than ignored: there is no unambiguous split between
front and back, and picking one silently is the kind of choice that is wrong half the time.

## Fifth slice — 2026-08-28

Or-patterns and `as`. `(Lit(0) | Lit(1))` takes one arm for either shape, alternatives nest
wherever a sub-pattern goes — `Add((1 | 2), r)`, `[(1 | 2), b]` — and `as` keeps the whole
value alongside the parts: `Add(l, r) as whole`, and the item's own example,
`(Lit(0) | Lit(1)) as lit`.

### The separator stays `|`, decided rather than inherited

`|` is the pipeline everywhere else in this language, and the list-pattern slice had just
argued that consistency with the language beats borrowing from other ones — so the choice was
put explicitly, against `or` and `||`, which are what tosh already spells logical-or.

`|` won on collision. A `|` in pattern position is a parse error today, so adopting it changes
no existing arm. `or` and `||` would reinterpret `($a or $b)` — an arm that runs today,
evaluating a boolean and comparing it to the subject — as two alternatives, which gives a
different answer whenever the subject is false and either side is true. `|` is also what Rust,
C# and F# spell it, so a reader arriving from any of them already knows it.

### Every alternative must bind the same names

Checked where it is written. `(Lit(v) | Add(l, r))` would leave `$v` unset whenever the `Add`
side matched, and an unset variable is not an error anywhere else in the language, so the arm
would simply do something wrong. This is the only place it can be caught.

### Binding asks the matcher which alternative won

Matching and binding were two passes, and nothing recorded which alternative had matched. The
obvious shortcut — bind from the first alternative whose *shape* fits — is wrong:
`(Point(a, 0) | Point(0, a))` has one shape and two meanings, so it would bind zero half the
time. So the binding walk became async and asks the matcher, which is right in both directions.
The negative control that binds from the first alternative fails exactly that test.

**Alternatives are a destructuring pattern or a single token.** Handing a general expression to
`ParseArgument` would let it read `0 | Lit(1)` as a pipeline and swallow the separator. A test
that needs more than one token goes in a guard, where the whole expression grammar is available
and `|` means what it means everywhere else.

## Sixth slice — 2026-08-28

`§Match Expressions` documents the whole grammar. The Pattern Forms table gained a
destructuring group — the six new forms with the rules that are easy to get wrong beside them:
the paren must abut but the brace need not, a string is not a sequence, a rest may sit in the
middle and there may be only one, `as` follows a destructuring form only.

A `Destructuring Patterns` subsection follows it with the union-evaluator example the item
opens with, written the way it could not be written before, and a second listing showing the
forms composing in one `match`. The two rules checked where a pattern is *written* rather than
where it runs — one rest, and alternatives binding the same names — are called out in a note,
since they are the only two places the language reports a pattern rather than failing to match.

Both listings were run before being written down. The evaluator folds
`Add(Add(Lit(1), Lit(2)), Lit(4))` to 7; it is spelled `evaluate` rather than `eval` because
`eval` shadows a builtin and warns, which a reader copying the example would have hit.

Spec rebuilds clean at 330 pages with no undefined references.

## Seventh slice — 2026-08-28

Shadowing is diagnosed. A pattern binding that covers an enclosing variable now warns where it
is written, naming what changed: *`$v` means the bound field for the rest of this arm*. It is a
**warning**, because shadowing is legal and sometimes meant — what it prevents is the silent
version, where an arm reads the bound field in the places that meant the outer variable and
nothing says the name changed meaning.

The binder gets a scope per arm, which it did not have. Without one, the second arm to reuse a
name would look like it shadowed the first, and every `Ok(a) / Err(a)` pair would warn.

### Two binder defects had to be fixed to get here

**The binder only walked `CommandSyntax` stages.** `ExpressionPipelineStageSyntax` and
`PipeForwardStageSyntax` were skipped outright, so nothing inside a bare `$x + 1`, a
`| where { … }`, or *any* `match` was ever bound-checked — not the arms, not even the subject.
Those came back as `tosh.runtime.unknown_variable` when the arm happened to run, rather than
`tosh.bind.unknown_variable` where they were written. The `MatchArgumentSyntax` case in
`VariableBinder` had been unreachable the whole time. Widening the walk left the suite green.

**`Strict` threw the whole diagnostic batch regardless of severity.** So the first warning-only
run rendered a warning, exited 0, and never executed the program — strictly worse than not
warning at all, and it would have made every future binder warning unusable. Severity is now
honoured: warnings are reported, errors still throw.

### The `hush` footer is advertising something that does not work

The renderer appends *hush: <code>* to every coded diagnostic, but binder diagnostics do not go
through the hush check — an in-source `hush` cannot help, since the binder runs before any
statement executes, and a `hush` that runs before a `source` does not suppress it either. The
help text here does not promise it. The footer is the renderer's own, on every binder
diagnostic, and predates this item.

## Eighth slice — 2026-08-28

The field diagnostic moved to binding time. The seventh slice recorded this as blocked on
`TOAST-0052` — a `union` is an ordinary statement evaluated at runtime, so there was no
declaration for the binder to check against. That was the wrong read: the binder already
collects same-source *function* declarations from the syntax, and union variants and record
fields can be collected the same way.

So they are. A pattern naming a variant or record declared in this source now has its fields
checked where it is written: `Lit { valu }` and `Lit(a, b)` are reported even when the arm never
runs, which is exactly the case the runtime check cannot reach. Nested patterns are checked too.

**Same-source only, deliberately.** A type from a `require`d file is not collected, and a pattern
naming one is left alone rather than guessed at — a missed check costs a runtime diagnostic that
still names the field, while a false one costs a program that will not run. Seeing another
file's declarations is `TOAST-0052`'s work. The runtime checks stay for that reason, and for
classes and structs, which are not collected either.

The tests for this had to ask for `BinderStrictness.Strict`, the way the CLI runs a script. The
default is `Warn`, under which a binder diagnostic is written to the error stream rather than
thrown — so the first version of these tests passed whether or not the check existed.

## Acceptance

- [x] Variant patterns bind fields positionally — `Ok(v)`, `Add(l, r)` — **interpreted**
- [x] Variant patterns bind fields by name, with shorthand — `Lit { v }`, `Lit { v: got }`,
      and a literal or `$variable` on the right to test rather than bind — **interpreted**
- [x] Record and class patterns bind fields by name — and structs, and records positionally.
      A class is named-only by design; see the third slice
- [x] List patterns with a rest binding — spelled `[first, ...rest]`, since `..` is the
      range operator; the rest may also sit in the middle, and binds an array
- [x] Patterns nest to arbitrary depth — `Some(Add(Lit(a), Lit(b)))`, mixing both forms
- [x] Or-patterns, and `as` to bind the whole while destructuring the parts — `|` kept as the
      separator against `or` / `||`, decided in the fifth slice
- [x] Bound names are scoped to their arm, and shadowing an enclosing variable is warned
      where it is written — a warning, since shadowing is legal; see the seventh slice
- [x] A pattern naming a field the variant does not have is a **binding-time** diagnostic
      naming the field — for types declared in the same source; the runtime check stays as the
      backstop for `require`d types, classes and structs. See the eighth slice
- [x] Guards compose with bindings — the guard sees the bound names, **interpreted**
- [ ] Interpreted and compiled agree, in the differential corpus — **not started, and not
      next.** Compiled tosh is an experiment until the interpreted language is solid
      (`docs/ROADMAP.md`, *Standing Priority Decision*), so no new surface is added there.
      Every pattern node is refused by name — `ListPatternSyntax`, `OrPatternSyntax`,
      `BoundPatternSyntax`, `VariantPatternSyntax` — which is the right failure, and ordinary
      patterns still compile, so the compiler guards stay green
- [x] `§Match Expressions` documents the full pattern grammar in one table, with a
      `Destructuring Patterns` subsection and both listings run before being written down
