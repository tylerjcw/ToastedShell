---
id: TOAST-0018
title: "Portable core semantics: the eight Phase A concerns outside formatting and streaming"
status: complete
area: toast
priority: 2
opened: 2026-08-17
closed: 2026-08-20
supersedes: TS-P3-16
---

## Problem

`SELF_HOSTING_RFC.md` Phase A names ten concerns to specify:

> equality, hashing, ordering, nullability, overflow, Unicode, formatting, collection
> shape, streaming, and exception semantics

**Phase A is scoped to formatting and streaming** — `TOAST-0014`, `TOAST-0017` and
`TOAST-0015`. This item carries the other eight, so that scope decision does not quietly
lose them.

`TS-P3-16` was the RFC's placeholder for this and is one paragraph. It is superseded here
rather than expanded, because new work does not go into the `TS-P*` system.

## What exists today

Measured in the Phase A survey (`docs/plan/PHASE_A_SURVEY.md`):

| Concern | Where it lives | State |
|---|---|---|
| equality | `OperatorEvaluator.AreEqual` (`:321`) | implemented, unspecified |
| ordering | `OperatorEvaluator.EvaluateOrderedComparison` (`:474`), `TryCompareByName` (`:562`), and a second path at `ToshEngine.cs:2609 CompareCore` | **two implementations** |
| hashing | no central site | **absent** |
| nullability | scattered; `ToshTruthiness.cs` (85 lines) covers truthiness only | partial |
| overflow | no `checked` policy in `OperatorEvaluator` | **unspecified** |
| Unicode | inherited from `System.String` wholesale | **unspecified** |
| collection shape | `TS-P3-04`, status *research*, one-line acceptance | filed, not designed |
| exception semantics | `ToshError.cs` (73 lines) over .NET exceptions | partial |

`TypeConversion.cs` (748 lines) and `OperatorEvaluator.cs` (1,679) are where most of this
actually lives.

## Acceptance

- [x] **Equality** specified in Tōast terms: which values are equal, across numeric widths,
      `null`, records, collections and class instances — not "whatever `AreEqual` does" —
      **done 2026-08-20**, see below
- [x] **Ordering** specified, and the implementations reconciled — **done 2026-08-19**,
      and there were **three**, not two: `OperatorEvaluator` for `<`, `SortCommand`'s
      `ShellSortComparer`, and a simplified copy of the latter in `ToshEngine` for the
      fused `sort | first` path. One comparer now lives in `Tosh.Runtime`; see below
- [x] **Hashing** given a contract consistent with equality — **done 2026-08-20**, and the
      box's premise turned out to be the finding: there was nothing to be consistent
      *with*. See below
- [x] **Nullability** specified beyond truthiness: what `null` means in comparison,
      arithmetic, member access and collection membership — **done 2026-08-20**, see below
- [x] **Overflow** given a policy — **done 2026-08-20**, and the policy turned out to be a
      fourth option the box did not list: integer arithmetic *promotes*. See below
- [x] **Unicode** specified: what a `str` is made of, what `Length` counts, and how
      indexing, slicing and comparison behave — **done 2026-08-20**, see below
- [x] **Collection shape** resolved with `TS-P3-04` — **specified 2026-08-20**; the
      asymmetry it names is the half worth keeping, and the half that is a defect is
      decided and filed as `TOAST-0028`. See below
- [x] **Exception semantics** specified: what is catchable, what a thrown non-error value
      means — **done 2026-08-20**. The `no_clr` half is `TOAST-0029`; see below
- [x] Each of the eight lands in `docs/spec/` as prose *before* implementation, and in the
      backend-neutral corpus after — **done 2026-08-20.** The discipline held: the Unicode
      section was *wrong* when written and its corpus caught it before the commit
- [x] The corpus extends `DifferentialExecutionTests`' pattern — one program, interpreted
      and compiled, asserted equal — rather than starting a second harness — **done
      2026-08-20**, and it found five divergences on first use. See below

## Ordering — 2026-08-19

Specification first (`§Ordering`), corpus from the prose (`ValueOrderingTests`, 34 tests),
then the change. Three findings, each measured:

**`<` was not portable.** String comparison fell through to `string.CompareTo`, which is
culture-sensitive: `("z" < "ä")` answered `false` under `en_US` and `true` under `sv_SE` —
the same program meaning different things on different machines, which is precisely what
Phase A exists to eliminate. .NET carries ICU's collation data, so the locale did not even
need to be installed for the answer to change. Strings now compare by **code point**.

**The decisive argument was not portability but consistency.** Equality compares two
strings exactly, so `"a" == "A"` is false — while a case-insensitive order calls them
*equal*. That is a broken trichotomy: two values neither less, nor greater, nor equal.
Ordering must agree with equality about which values are the same, and only code point
does. `Trichotomy_holds_against_equality` pins it as a property rather than as examples.

**The third implementation was disagreeing in production.** `[1, "a", 2.5] | sort`
answered `1, 2.5, "a"` while `| sort | first 3` answered `2.5, 1, "a"`: the fused copy
compared only values of an identical type and otherwise ordered by type *name*, putting
`Double` before `Int32`. Sharing one comparer makes that class of divergence unwritable
rather than merely fixed — which is what the box asked for.

### The operators and the sort differ by policy, and only there

An operator may refuse a pair with no meaningful order and say so; a sort may not, because
every element has to land somewhere. So `<` raises on booleans, on a string against a
number, and across two enums, while the comparer orders everything via a type-name
fallback. `null` is the same split: outside the order for operators — every direction
`false`, `null < null` included — and sorted first by the comparer. Both halves are
specified.

### `sort`'s default turned over

`sort` now orders by code point, with `-i`/`--ignore-case` to ask for the old behaviour.
`-o`/`--ordinal` is still accepted and now names the default, so a script that asked for
code-point order keeps getting exactly what it asked for.

This is a **visible daily change** for an interactive shell: `ls | sort Name` groups
capitalised names first (`AGENTS.md`, `Directory.Build.props`, … , `artifacts`) where it
used to interleave them. That was weighed and accepted — `TS-P2-75` had already recorded
the opposite complaint, that case folding put `expected_record_fields` before
`expected_record_field_default`, and generated output wants code point.

`sort -u` follows the same rule, so uniqueness and ordering cannot disagree about case.

**Negative control: 14 of 44 fail** with the three source changes reverted and the tests
kept. Suite 5,904 passing.

## Equality — 2026-08-20

`TOAST-0003` had already rewritten the cascade; this added what the cascade named nowhere —
numeric widths, `null`, class instances and the float specials — as
`§Numbers, null and Instances`, with `ValueEqualityTests` written from it.

**Equality was not transitive.** An integer compared against a floating value was decided
by conversion, and a 64-bit integer above 2^53 has no exact `double`:

```tosh
var a = 9007199254740993 as long
var c = 9007199254740992 as long
var b = ($c as double)
($a == $b)   # was true
($c == $b)   # true
($a == $c)   # false
```

A relation where `x == y` and `y == z` do not give `x == z` cannot back a dictionary, a
`distinct`, or a cache — so this is a defect in the value model rather than a rounding
wart. An integer now equals a floating value only when that value is finite, integral and
the same number. Converting the *float to the integer* is what makes it exact: an integral
double inside `long`'s range converts with nothing lost.

**The fix landed on the wrong implementation first, exactly as the file predicted.**
`OperatorEvaluator.AreEqual` and `ToshEngine.AreEqualAsync` are structurally parallel, and
`ToshEngine.Operators.cs` opens by recording that `TS-P1-14`'s fix "landed only on the
synchronous side — `==` goes through here, so the defect survived a change that looked
complete". Adding the rule to the evaluator changed nothing observable, the suite stayed
green, and only measuring `==` against the binary showed it. The engine now **delegates**
to the shared rule, the way it already delegates `TryCompareByName`.

`Both_paths_agree` is the guard that makes this fail rather than pass silently, and it
needed a case above 2^53 to have any force — with only ordinary values it passes while one
implementation carries the rule and the other does not. **Negative control, engine side
only reverted: it fails on exactly that pair.** Both sides reverted: 3 of 39 fail.

### Decisions recorded rather than inherited

**`NaN` equals itself**, which is deliberately not IEEE 754's `==`. Equality is the relation
collections are built on and has to be reflexive; under the IEEE rule a `NaN` in a
dictionary could never be found again. Signed zeroes follow IEEE and compare equal.

**A class instance is equal only to itself** unless the class declares `equals`, which is
the opposite default from a `record` — a record is a bag of values, a class has identity.

Suite 5,943 passing.

## Hashing — 2026-08-20

The box asked for "a contract consistent with equality". Measuring found **no hash could
be**, and that is the result rather than an obstacle to it.

**`==` is coercive, and coercion makes it intransitive:**

```tosh
("1" == 1)         # true
(1 == "1.0")       # true
("1" == "1.0")     # false   — two strings compare exactly
```

Three values, two of them equal to a third and not to each other. A relation with no
equivalence classes has nothing for a hash table to bucket, so writing a hash function
would not have fixed anything.

**The symptom was a dictionary that answered by insertion order.** Two dictionaries built
from the same two pairs in opposite order returned *different values for the same lookup*,
because lookup is a linear scan with `==` and stopped at whichever mutually-equal key it
reached first. The box's own phrase — "cannot back a portable dictionary" — was literally
true and demonstrable in three lines.

### The decision: two relations

`==` stays coercive, because that is what a shell wants when values arrive from commands
as text. Containers use **key equality**: same value, no cross-type coercion, therefore
transitive and hashable. Specified as `§Key Equality`. A value can be `==` to a key it
does not match *as* a key, and the specification says so plainly.

`ShellKeyComparer` is the one implementation, and six surfaces now share it: dictionary
lookup, `distinct`, `sort -u`, `frequencies`, `group-by` and set literals. Each had
improvised its own key before — a JSON rendering in three of them (field-order sensitive,
so two records `==` called equal survived `distinct` as separate values), CLR
`GetHashCode` in another, a linear `==` scan in the last.

### Two rules that only measurement produced

**A type that defines its own equality decides, and that check must precede the structural
one.** A class instance is an `IShellRecordObject` and therefore record-*like*, so the
structural path folded two distinct instances holding equal properties — when a class
without a declared `equals` is a key only to itself. Caught by a probe that expected 3 and
got 2.

**A class declaring `equals` without `hash` now hashes to a constant.** The reference hash
it had would have put two instances its own `equals` calls equal into different buckets,
so a container would hold both. One shared bucket is correct — slower within it, never a
wrong answer — and declaring `hash` restores O(1). Fixed in `ToshClassInstance.GetHashCode`
so it holds for every consumer, not only this comparer.

### Blast radius, and what that says

**The full suite passed unchanged** after making dictionary lookup non-coercive — which
means nothing in 5,943 tests covered the coercive behaviour. It was untested, not merely
unspecified.

**Negative control: 7 of 31 fail** with the routing reverted and `ShellKeyComparer` left in
place, which isolates the wiring from the comparer. Suite 5,974 passing.

### Not done: lookup is still linear

A dictionary is still scanned, so lookup is O(n) — correct and order-independent now, but
not fast. Making it O(1) means constructing the underlying dictionary *with* the comparer,
which is a change to how `{% ... %}` is built rather than to what equality means. Filed as
the remaining half of this concern rather than done quietly.

## Nullability — 2026-08-20

Specified as `§What null Means`, with `NullSemanticsTests` written from it. The unifying
rule: **an operation with no sensible answer for a missing value reports that**, and the
author asks for propagation where they want it.

Comparison was already settled by the three earlier concerns, and the three answers differ
deliberately — `null` is equal only to itself, *outside* the order entirely (every ordered
comparison false, `null < null` included), and its own key. A test pins all three together,
because that is the kind of difference someone tidies into agreement without reading why.

Three behaviours were changed rather than written down:

**Reading a member of `null` answered `null` silently — for any member name.** So
`$x.Lenght` reported nothing when `$x` was null and raised when it was a string: the same
typo, silent or loud depending on data. It also left `?.` meaning nothing, since `.` and
`?.` behaved identically. `.` now joins method calls and indexing, which already raised on
a null receiver, and `?.` is how propagation is asked for.

Worth recording: the MCP metadata already described `?.` as yielding null "instead of
failing", which was **false** — `.` did not fail. The implementation caught up with the
documentation rather than the other way round.

**`null + "a"` was `"a"`** while `null + 1` raised. A missing value turning into empty text
is how a null reaches a log line, a filename or a command argument with nobody noticing it
was missing. Both now raise, and `($x ?? "") + "a"` is the opt-in.

**`"abc" contains null` was `true`**, because `null` rendered as `""` and every string
contains that. Now false: a string contains text, and `null` is not text. Collection
membership is untouched and still correct — `null in [1, null]` and
`[1, null] contains null` both hold, because that asks a different question.

`contains` needed fixing in **both** parallel implementations. The engine's copy is the one
a script reaches — the same shape that let the exact-numeric fix land on the wrong half —
and a test now asserts the two agree.

**The full suite passed unchanged** after all three changes, which again says the old
behaviour was untested rather than merely unspecified. The real shell config loads and runs
normally. **Negative control: 8 of 28 fail** with the source reverted. Suite 6,002 passing.

## Overflow — 2026-08-20

The box offered wrap, saturate or raise. Measuring found a fourth policy already in place
and better than all three: **integer arithmetic promotes to arbitrary precision.**
`int.MaxValue + 1` is `2147483648`, not `-2147483648`. Wrapping is what most languages
inherit from the machine, and it turns a number that was merely large into one that is
merely wrong — usually negative, usually far from where the mistake happened.

So `+`, `-` and `*` needed no decision, only writing down. The rest of the picture is
coherent with it and now specified: a variable with a declared type is still bounded, so
the *expression* `$x + 1` is exact while assigning it back into an `int` raises; `as`
refuses an out-of-range conversion rather than truncating; floating point follows IEEE,
with overflow giving an infinity; and integer division by zero raises, because there is no
integer answer to give.

**`**` was the exception and is fixed.** It computed through `Math.Pow` and dropped to
`double` as soon as the result left `int` range, so `2 ** 62` lost its low bits although
the exact value fits a `long` and `2 * 2 * …` would have promoted. Raising to a power is
repeated multiplication and had no business losing precision where multiplying would not.
A fractional or negative exponent is a different operation and still gives a `double`, and
an exponent past a million falls back to floating point — a memory bound rather than a
preference, since `BigInteger.Pow` allocates in proportion to the exponent.

### A hole this session opened, found by surveying

Key equality reduces a number to what decides its identity, and `BigInteger` was not in
that switch — so a *promoted* `2147483647` was `==` to the ordinary one and a **different
key**. One value, two dictionary entries. The old JSON-string key had folded them by
accident, so this was a regression introduced by the hashing commit and caught two commits
later only because overflow promotes into exactly that type.

A `BigInteger` inside `long` range now normalises to `long`, which is what keeps the hash
consistent; nothing else can equal one outside that range.

### Noticed while writing the corpus

The narrowing in `Power` was written `cond ? (int)exact : exact`, and C# made the
conditional's common type `BigInteger` — `int` converts to it implicitly — so the cast was
undone on the way out and `2 ** 10` answered a `BigInteger`. Only measuring the *type*
rather than the value showed it.

**Negative control: 4 of 24 fail** with both source files reverted. Suite 6,026 passing.

## Unicode — 2026-08-20

Decided: a `str` is a sequence of **UTF-16 code units**, and comparison does **not**
normalise. Both keep what the runtime already did, so this concern changed no behaviour —
`UnicodeSemanticsTests` is what makes "unchanged" into something a backend can be held to.

`Length` counts code units, so a waving hand is 2 and a three-person family is 8.
**Indexing can therefore return half a character**, and the specification says so rather
than leaving it to be discovered: `$w[1]` on `a👋b` is an unpaired high surrogate — a valid
`Char` that is not valid text.

The alternatives were weighed and are recorded in the notebox. Scalar values would make
indexing whole at the cost of O(n) and a `Length` disagreeing with every .NET API the same
string is passed to. **Grapheme clusters** — what a person means by a character — were
rejected for a reason this phase has already used once: segmentation depends on the Unicode
version, so two implementations built against different ICU releases would disagree about
the length of one emoji. That is the same defect that made culture-sensitive collation
unacceptable for ordering, and worse in a length than in a comparison.

Normalisation stays explicit (`Normalize`, `IsNormalized`, both already reachable). The
trap that leaves is documented rather than smoothed over: macOS hands out filenames
decomposed and Linux composed, so the *same filename* from two machines compares unequal,
sorts apart and counts as two distinct values.

### The specification was wrong before the corpus was run

It said `"\uHHHH"` writes a code unit. It does not. There are **two escape tables** —
`ReadEscapeSequence` for `"..."` and `ReadAnsiCEscapeSequence` for `$'...'` — and only the
second knows `\u` and `\x`. In a double-quoted string, `"\u00E9"` is six characters, kept
silently.

**Every probe had gone through `tosh -c`, and the command line does its own shell-level
escape processing**, so `tosh -c 'echo ("\u00E9".Length)'` answers `1` while the identical
line in a script answers `6`. The measure-first discipline was followed and still produced
a false claim, because the instrument was not the language. Corrected throughout; every
example now uses `$'...'` and was run from a **file** before being written down.

Filed as `TOAST-0027`, including the sharper half: `-c` and a script file disagree about
what a string literal means.

### No negative control, and why

Nothing was changed, so there is no fix to revert. The corpus's value is as a guard against
future drift — and it demonstrated it can find drift before it was even committed, by
catching the escape claim. Suite 6,041 passing.

## Collection shape — 2026-08-20

`TS-P3-04` was one line: "remove cardinality lookahead while preserving object-valued
pipelines and a reasonable migration path", with `[1,2,3] | count` being 3 while a piped
dictionary counts as 1. Measuring split that into two rules, one settled and one broken.

**Which values are sequences is settled, and the asymmetry is right.** An array, a set and
a range spread; a dictionary, a record and a string are single values. A dictionary is a
value with named parts rather than a sequence of them, so counting it as 1 is correct
rather than an inconsistency to be removed. Specified as `§Collection Shape`.

**Which position a collection is in is the defect.** A sequence arriving *alone* expands;
several are left as items. So producing more data changes what the earlier data meant:

```tosh
func a() { yield [1, 2, 3] }
func b() { yield [1, 2, 3]; yield [4] }
a | count    # 3 — the elements        a | first    # 1
b | count    # 2 — the arrays          b | first    # [1, 2, 3]
```

And deciding it requires reading one item further than the consumer asked for. Measured: a
generator behind `first 1` runs **two** steps, so any side effect of the second happens for
an item nobody requested.

**Decision: the producer decides shape, not the consumer** — a literal or variable spreads,
a command or generator yields its collection as a value. Filed as `TOAST-0028` rather than
done here, because it changes how every stage receives input and needs its own corpus.
`TS-P2-74` records why the obvious version fails: spreading every list-valued head made
`[] | to json` send nothing downstream, and eight tests said so. The mechanism that can
carry it already exists unused — `PipelineInputAttribute`'s `AcceptsList`, and
`PreExpandedSequence` as the inverse marker.

### Two tests assert the defect deliberately

`CollectionShapeTests` pins the wrong behaviour *and says so*, because an unlabelled test
of a known defect is one the next reader silently "fixes". When `TOAST-0028` lands,
`Producing_more_data_changes_what_the_earlier_data_meant` is the one that must flip. The
`[] | to json` case is kept beside them as a control so the fix cannot regress it.

The specification names `TOAST-0028` in the section, so prose and defect cannot drift
apart. Suite 6,053 passing.

## Exception semantics — 2026-08-20

Specified as `§Errors and catch`, with `ErrorSemanticsTests` from the prose. Anything can
be thrown and arrives unchanged; there is no typed `catch`, so one handler receives
everything and discriminates with `is`; `finally` always runs, inner before an outer
`catch`; `throw $e` re-raises; `try` is a statement, not an expression.

### Two defects, both found by one probe

**A class declared `extends Error` was not `is Error`.** The CLR base was matched by *name*,
and `Error` is the alias for `ToshError` — so the spelling a class was declared with was
the one spelling that did not match. The consequence was not cosmetic: `is Error` is the
only way to tell a raised error from an arbitrary thrown value, so a user-defined error
landed in the same bucket as a thrown string.

`DotNetTypeResolver` already records the alias for exactly this purpose, and the comment
beside its `error` entry *already claimed* `catch (e) { $e is NativeError }` worked.

**And the CLR base was consulted only on the instance's own definition**, after the
declared-class walk rather than at each level of it. So two levels of inheritance from a
built-in matched nothing at all: `E2 extends E1 extends Error` was not `is Error`,
`is ToshError` **or** `is Exception`. That one was pre-existing and invisible while the
first bug hid it — fixing the alias at depth 1 is what made the depth-2 hole measurable.

**Negative control: 5 of 15 fail** with the fix reverted. Suite 6,068 passing.

### The `no_clr` half is filed, not answered

A raised runtime error — division by zero, an index out of range, a member of `null` — is a
*diagnostic* rather than an `Error`, and that distinction is intended: one is the language
reporting an operation had no answer, the other is a program raising something on purpose.
What is not settled is the **spelling**. A diagnostic answers only to the implementation
type name it happens to have, so `$e is ToshDiagnosticException` is true and
`$e is Exception` is false, and a target without the CLR has nothing to call it.

That sits inside a wider defect in `is` itself, filed as `TOAST-0029`: for a CLR value `is`
matches the exact type name and nothing else, so `[1,2] is IEnumerable` and
`$ex is Exception` are always false, while a *declared* class instance walks its bases,
interfaces and traits correctly. The two halves of one operator disagree about what it
means.

It is filed rather than fixed because the obvious repair contradicts a rule already
specified: `string` implements `IEnumerable<char>`, so pure CLR assignability would make
`"abc" is IEnumerable` true while `§Collection Shape` says a string is an atom. That needs
a decision and an exception list, not a one-line change.

## The backend-neutral corpus — 2026-08-20

Phase A's exit: "core behaviour is specified in Tōast terms and enforced by a
backend-neutral corpus". `DifferentialExecutionTests` gained a representative subset of all
eight concerns — twenty-two cases, two to four each, chosen for the property each
specification turns on rather than for coverage.

**It found five divergences on first use**, filed as `TOAST-0030`: a dictionary counts as 1
interpreted and 2 compiled; `class E extends Error` does not compile at all; `is Error` is
false compiled; and the two null messages differ. Three of those are semantic — a program
that runs correctly interpreted gives a different *answer* compiled.

The finding that matters more than the five is that they existed. Eight concerns were
specified and given a corpus, and every one of those corpora ran a **single backend** — so
the specifications described the interpreter, and only running them across both showed
where that was not the same thing as describing the language.

### The harness was measuring two different things

`RunInterpreted` compared `value.ToString()` against the compiled side's captured *stdout*,
which is rendered. So every boolean read `True` interpreted and `true` compiled: a probe of
the eight concerns reported **fifteen** divergences, of which **ten were the harness**.

The interpreted side is now reduced through `ToastRenderer` — the contract `TOAST-0014`
established for exactly this purpose, and the one both backends are supposed to meet. The
existing corpus never noticed because its cases all produce strings from interpolation,
where `ToString` and the rendering coincide.

`Interpreted_and_compiled_agree` also folds a failure into the compared value, as the
divergence test already did. Two backends raising the same error is agreement, and it is
agreement the specification asks for — `§Overflow` requires integer division by zero to
raise, and a corpus that cannot express "both raise, identically" cannot check it. A
differing message is still a divergence, because the message is compared.

### What was handed on

All eight concerns are specified in `docs/spec/`, each with a corpus written from the
prose, and a representative subset of all eight runs on both backends.

Five of the eight carried behavioural fixes, each with a negative control: ordering became
portable and gained one implementation in place of three; equality became transitive;
containers gained a relation they can hash; reaching into `null` reports it; and `**`
promotes with its neighbours. Two are specified with their defects deliberately pinned *as*
defects, so the tests fail when someone fixes them — `TOAST-0028` (collection shape depends
on cardinality) and `TOAST-0029` (`is` matches a CLR value's exact name only).

Four items were filed on the way and are the honest remainder of this work:

- `TOAST-0028` — the producer should decide collection shape, not the consumer
- `TOAST-0029` — `is` should mean the same thing for a CLR value as for a declared class
- `TOAST-0030` — five specified semantics the compiled backend does not implement
- `TOAST-0026`, `TOAST-0027` — a decimal literal's precision, and a silently-kept escape

**Phase A's exit is met**: core behaviour is specified in Tōast terms and enforced by a
backend-neutral corpus. What that corpus immediately showed is that the specifications had
been describing the interpreter — which is the reason the criterion is written the way it
is.

## Notes

**Not Phase A, and deliberately not started.** The scoping decision is recorded in
`DECISIONS.md` for 2026-08-17: formatting and streaming first, because they are what block
`TOAST-0006` and what the survey found to be cheapest and best understood.

This item is the reason that decision is safe. Eight concerns with no owner is how a phase
exits "complete" while its stated goal is unmet.

Sequence after Phase A. Several of these are more expensive than they look — reconciling
two ordering implementations is a behavioural change, and an overflow policy changes
arithmetic results — so each wants the same treatment `TOAST-0014` is getting: specify,
then move, never both inside one diff.
