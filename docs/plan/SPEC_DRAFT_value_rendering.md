# Spec draft — Value rendering

*Phase A, stage 1. Draft for review, 2026-08-17. Not yet in `docs/spec/toastscript-spec.tex`.*

This is the contract `TOAST-0014`, `TOAST-0017` and `TOAST-0015` are all downstream of:
**what string does a Tōast value produce, and who decides.** Sections 1–8 are normative and
are what would land in the spec. Appendix A is the measured gap between this and today, and
Appendix B is what I could not decide alone.

---

## 0. This is a language question, not a shell question

Two different things happen to a value, and only the first is specified here.

```tosh
var xs = [1, 2, 3]

$xs                  # DISPLAY — TōSh paints a table on a terminal
                     # ┌───┬───┐
                     # │ 0 │ 1 │  … not specified here, not changing

var s = $"{$xs}"     # RENDERING — a Tōast program builds a string
                     # "1 2 3"    … this is what this document is about
```

The second one is the value of a **language expression**. The program can write it to a
file, send it over a socket, compare it, or return it. Nothing about a terminal is
involved, and a `no_clr` Tōast program with no shell at all still has to know what it is.

**The reason this currently looks like a shell question is the defect.** `$"{x}"` is
language syntax, but its result is produced by `ObjectFormatter`, which is constructed from
a `DisplayProfileRegistry` and changes when `$tosh.Config.Display` changes. So today the
string a program builds really is decided by the shell. This document is how that stops.

Everything measured in Appendix A was measured through the TōSh CLI, because it is the only
host that exists. The values measured — lists, records, enums, tuples — are **language**
values, and the strings recorded are what a program gets back, not what a terminal shows.

## 1. Scope

**Rendering** is the language operation that turns a value into a string. It is reached by:

- a bare interpolation hole — `$"{$x}"`
- a hole with a format clause — `$"{$x:F2}"`, `$"{$x,6}"`
- value-to-text conversion — `$x as string`, and the same coercion wherever a string is required
- a thrown value's text inside a diagnostic

**Rendering is not display.** Display is how TōSh paints a value on a terminal — tables,
colour, column widths, profiles, themes. Display may call rendering; rendering must never
call display, and must never consult display configuration.

The rule this exists to establish:

> A Tōast program produces the same strings on every target, in every host, under every
> configuration. `$"{x}"` in a program with no shell, no terminal and no profiles produces
> exactly what it produces in an interactive TōSh session.

This is a precondition for `no_clr`: a native target cannot carry the display stack, and a
language whose string output depends on it is not portable.

---

## 2. The rendering operation

Rendering takes a value and a **format**, and produces a string.

```
render(value)          → the value's default rendering
render(value, format)  → the value rendered per format
```

A bare hole is `render(value)`. A hole with a clause is `render(value, clause)`. **These are
the same operation with a different argument** — not two mechanisms. A bare hole is a hole
whose format is the value's specified default.

That framing is the whole design. It is also already half-true: the clause path today does
not consult display profiles and does not vary with configuration. The bare path is the one
that has to join it.

Rendering is **total**: every value renders. There is no value for which rendering fails,
because a diagnostic that cannot render the value it is about is worse than an imperfect
string.

---

## 3. Scalars

| Value | Renders as |
|---|---|
| `null` | `null` |
| `true`, `false` | `true`, `false` |
| integer | decimal digits, `-` prefix if negative, no separators, no suffix |
| floating | shortest form that round-trips to the same value |
| NaN | `NaN` |
| positive infinity | `Infinity` |
| negative infinity | `-Infinity` |
| negative zero | `-0` |
| `char` | the character itself, unquoted |
| `string` | see §4 |

Notes on the ones that are decisions rather than obvious:

**Shortest round-tripping form** means `0.1` renders `0.1`, not `0.1000000000000000055`.
It also means a floating `3.0` renders `3` — indistinguishable from the integer `3`.
That is deliberate: `$"{$price}"` producing `3.0` when the value is a whole number reads as
a bug to everyone who has not thought about types. The type distinction is available through
a format clause when it is wanted.

**Negative zero renders `-0`,** and this is worth stating because it is the kind of thing an
implementation loses by accident. `-0` and `0` are different values; a rendering that
collapsed them would make a program's output depend on how a zero was reached.

**No thousands separators, ever.** Grouping is a locale and display concern. A program that
wants `1,000,000` asks for it with a clause.

**No locale.** Rendering is invariant. A machine configured for a comma decimal separator
renders `3.14`, not `3,14`. A program's output must not depend on where it runs.

---

## 4. Strings

A string renders as **its own characters, unquoted, unescaped**, at the top level:

```
$"{"hi"}"        → hi
$"{""}"          → (empty)
$"{" x "}"       → " x " with its spaces
```

**Nested inside a container, a string is quoted and escaped.** `["a b", "c"]` renders
`["a b", "c"]`, not `a b c`. Without quoting, a container of strings is ambiguous — the
reader cannot tell two elements from one containing a space, and cannot tell the string
`"null"` from the value `null`.

Escaping inside quotes covers `"` and `\`, and the control characters that would otherwise
break the line: newline as `\n`, carriage return as `\r`, tab as `\t`.

This asymmetry — bare at the top, quoted when nested — is the standard one (Python's `str`
versus `repr`) and it is right for the same reason: the top-level case is "put this text in
my sentence", and the nested case is "show me this structure".

---

## 5. Containers

Every container renders in **its own literal syntax**, at every depth, with elements
rendered as nested values:

| Kind | Renders as |
|---|---|
| list / array | `[1, 2, 3]` |
| empty list | `[]` |
| dictionary | `{% "a" => 1, "b" => 2 %}` |
| set | `{: 1, 2 :}` |
| record | `{\| Name = "a", N = 1 \|}` |
| tuple | `(1, "a")` |
| range | `1..3` |

Two rules make this coherent, and both are departures from today:

**Uniform at every depth.** A list renders `[1, 2, 3]` whether it is the whole hole, an
element of another list, a dictionary value, or a record field. Today it renders three
different ways depending on where it sits (Appendix A.2), which is the single worst thing
in the current behaviour.

**No type names.** `Int32[]`, `System.Int32[][]` and `Object[]` must never appear. They are
BCL names, and Phase A's requirement is behaviour "independent of BCL names". A container's
shape is carried by its delimiters, which is what the delimiters are for.

**A range renders as a range**, not as its materialised elements. `1..3` is a value, and
rendering it as `1 2 3` loses the distinction between the range and the list it would
produce. A program that wants the elements asks for them.

---

## 6. Named values

**Enum member** renders as its member name: `Color.Red` renders `Red`.

A flags enum whose value combines members renders the member names in declaration order,
comma-separated: `Video, Audio`. A combination the declared members do not exactly cover
renders the underlying number.

**Class instance** renders as `TypeName { Field = value, … }` — the type name, then its
readable state in record-field syntax:

```
R { N = 5 }
```

The type name is required. Today a class renders `{| N = 5 |}`, which is *exactly* a record
literal, so a class and a record with the same fields render identically and a reader cannot
tell a `Point` from a `{| X, Y |}`.

**A type controls its own rendering by implementing the `Display` trait** — decided
2026-08-17:

```tosh
trait Display { func render() -> string }

class Temperature uses Display {
    prop Celsius: dec = 0
    func render() -> string => $"{$this.Celsius}°C"
}
```

A trait rather than a magic method name, because rendering is a *capability a type
declares*: the compiler can check it, a native target can dispatch it without reflection,
and it reads as a contract rather than a convention. `ToString` continues to work as a
fallback so no existing class breaks, but `Display` is what the spec teaches.

**Traits do not apply to CLR-backed values.** `42 is Show` is `false` even with an
`extend Int32` supplying the member. That is why §3–§5 specify built-in rules for scalars
and containers: the trait is the *user* extension point, layered above rules the renderer
always has.

> **`TOAST-0019` closed 2026-08-17**, so `trait Display { func render() -> string }` parses
> exactly as written above. The item's original claim — that a return type could not be
> declared at all — was wrong; it could, spelled `: T`. The real defect was that traits
> accepted different syntax from every other declaration, and they now share the same
> parser helpers.
>
> **Still open, `TOAST-0020`:** the declared return type is not *enforced*. A class may
> satisfy `render() -> string` with an implementation returning `int`. Not blocking — a
> wrong return type fails where the renderer uses it — but the contract is a declaration
> rather than a guarantee until that lands, and enforcement needs a variance rule written
> down first.

**A class with no `Display` and no `ToString`** renders as `TypeName { Field = value, … }`
— decided 2026-08-17:

```
R { N = 5 }
```

The type name is required. Today a class renders `{| N = 5 |}`, which is *exactly* a
record literal, so a class and a record with the same fields render identically and a
reader cannot tell a `Point` from a `{| X, Y |}`.

**Struct instance** follows the class rule.

---

## 7. Depth and cycles

Rendering is bounded. Both limits exist today and need stating rather than inventing.

**Depth.** Beyond a maximum nesting depth, a container renders as its delimiters with an
elision marker: `[…]`, `{% … %}`. The limit is a fixed number specified here, not a
configurable one — a configurable depth would make output depend on configuration, which is
the thing this document exists to prevent.

**Cycles.** A value already being rendered further up the current chain renders as an
elision marker rather than recursing. Detection is by reference identity, which is what the
implementation already does.

Neither case is an error. Rendering is total (§2).

---

## 8. Format clauses

A hole may carry alignment and a format: `{expr,align:format}`.

**Alignment** pads to a minimum width — positive for right, negative for left. Padding is
spaces. A value longer than the width is not truncated.

**Format** selects a rendering other than the default. The spec must name a **portable core
set** that every target supports, because "the value's own formatter, so every .NET format
string works" — what the spec says today — is precisely the BCL dependence Phase A removes.

Proposed core set:

| Kind | Specifiers |
|---|---|
| integer | `D`/`d` decimal, `X`/`x` hexadecimal, `B`/`b` binary, `O`/`o` octal, each with an optional minimum digit count |
| floating | `F<n>` fixed point, `E<n>` exponential, `G` shortest, `P<n>` percent |
| date/time | the pattern letters `yyyy MM dd HH mm ss fff`, plus `O` for the round-trip form |
| duration | `O` round-trip, and a pattern form |
| any | `?` for the nested (quoted, structural) rendering of a value that would otherwise render bare |

**A specifier outside the core set is target-defined.** On the .NET target it reaches the
value's own formatter, which preserves every format string that works today. On a portable
target it may be unsupported. A program that stays inside the core set renders identically
everywhere; a program that goes outside it has made a target-specific choice, and the spec
says so rather than pretending otherwise.

**A clause the value cannot honour is an error** — decided 2026-08-17. Today it is
silently ignored and the value renders plainly, which is the same silent-wrong-answer shape
as `TOSH-0001`: the program succeeds and produces text nobody asked for. `$"{$name:F2}"` on
a string is a mistake, and a mistake that renders is a mistake that ships.

This is a breaking change, and the failure lands at runtime in the middle of building a
string. It is accepted anyway: a clause is an explicit instruction, and ignoring an explicit
instruction is worse than refusing it.

---

## 9. What this does not cover

- **Display.** Tables, colour, column selection, terminal width, themes, `view`, display
  profiles. All of it stays in TōSh, and all of it may call rendering.
- **Serialisation.** JSON, TOML, and `ToshEngine.Pipelines.cs:270`'s write-to-a-redirect
  path may be a *different* contract — "how does this value go onto a stream" is not
  obviously "how does this value read in a sentence". Flagged in `TOAST-0015`; see Appendix B.
- **Parsing.** Rendering is one-way. Nothing here promises a rendered value parses back,
  though the container syntaxes were chosen so that most do.

---

## Appendix A — measured current behaviour, and every deviation

All measured 2026-08-17 against the Release CLI, exact bytes via `write-file`.

### A.1 What is already right

`null`, `true`, integers, `NaN`, `Infinity`, `-Infinity`, **`-0`**, strings at top level,
`char`, `Guid`, `BigInteger`, dictionaries, sets, records, and a class with a `ToString` all
render as this document specifies. Signed zero surviving is a genuinely good sign.

### A.2 A list renders three different ways depending on its container

| Expression | Today | Specified |
|---|---|---|
| `[1, 2, 3]` | `1 2 3` | `[1, 2, 3]` |
| `[[1, 2], [3]]` | `Int32[] [⏎  1⏎  2⏎] Int32[] [⏎  3⏎]` | `[[1, 2], [3]]` |
| `{% "k" => [1, 2] %}` | `{% "k" => System.Int32[] %}` | `{% "k" => [1, 2] %}` |
| `{\| Items = [1, 2] \|}` | `{\| Items = [⏎  1⏎  2⏎] \|}` | `{\| Items = [1, 2] \|}` |

**Three renderings of one value, one of which is a bare CLR type name with the contents
missing entirely, and two of which contain newlines.** This is the largest single gap and
the strongest argument for specifying before implementing.

### A.3 An enum leaks its implementation

```
$"{Color.Red}"
→ ToshEnumValue {
    Definition = Color
    EnumTypeName = "Color"
    Name = "Red"
    ShellTypeDescriptor = Color
    UnderlyingValue = 0
  }
```

Specified: `Red`. `TryFormatSimple` handles CLR `Enum`, but a ToastScript enum value is a
`ToshEnumValue` wrapper, which falls through to the generic object dump.

### A.4 A class is indistinguishable from a record

`new R()` with `prop N = 5` renders `{| N = 5 |}` — a record literal. Specified: `R { N = 5 }`.

### A.5 A tuple leaks `ValueTuple`

`(1, "a")` renders `{| Count = 2, Item1 = 1, Item2 = "a" |}`. Specified: `(1, "a")`.

### A.6 Strings are unquoted inside containers, inconsistently

`["a b", "c"]` renders `a b c` — two elements indistinguishable from one. But
`{% "k" => "v" %}` and `{| Name = "a" |}` **do** quote. So containers disagree with each
other about the same question.

`[1, null, 3]` renders `1 null 3`, indistinguishable from three strings.

### A.7 A range materialises

`1..3` renders `1 2 3`. Specified: `1..3`.

### A.8 `DateTime` — the two `TOAST-0017` faults

On a UTC−4 machine, `new DateTime(2026,8,17,12,0,0)` with `Kind = Unspecified`:

| | Today | Specified |
|---|---|---|
| `$"{$d}"` | `2026-08-17 08:00:00` | `2026-08-17 12:00:00` |
| `$"{$d:HH:mm:ss}"` | `12:00:00` | `12:00:00` |

The default path calls `ToLocalTime()`, and .NET's `ToLocalTime` treats `Unspecified` as
UTC. A wall-clock literal is shifted by the local offset, and the two holes disagree.

### A.9 Configuration changes what a program builds

```
$tosh.Config.Display.DateTime.ScalarMode = "Unix"
```
changes `$"{$d}"` from `2026-08-17 08:00:00` to `1786982400`, mid-script. The clause form is
unaffected. This is `TOAST-0014` proper.

### A.10 Interpreted and compiled can disagree

The compiled path renders through `ToshValueFormatter` → `new ObjectFormatter()`, whose
registry is built from a **fresh** `DisplayPreferences`. Interpreted uses the shell's live
one. They agree only while nothing is configured, and no test compares them.

### A.11 Scale of the change

**Nine of the deviations above change the output of a bare hole.** 160 existing test cases
across eleven files pin current behaviour, and an unknown fraction of them assert exactly
these strings. Triaging those — which assert language semantics, which assert display — is
the bulk of the implementation work, and it cannot be estimated before the contract is
agreed.

---

## Appendix B — what I could not decide alone

1. ~~**`ToString` or a Tōast-native name?**~~ **Decided 2026-08-17: a `Display` trait**,
   with `ToString` kept working as a fallback. Blocked in one respect — `TOAST-0019`, a
   trait member cannot declare a return type — so the contract's own signature is not
   currently expressible.

2. ~~**Is an unhonourable format clause an error?**~~ **Decided 2026-08-17: error.**

3. **Is serialisation the same contract?** `ToshEngine.Pipelines.cs:270` renders a value on
   its way to a redirect target. If `run-report > out.txt` should write the same text a hole
   would produce, it is one contract and that call site moves with the other three. If a
   stream wants a serialisation format, it is a second contract and that site stays behind.
   This decides whether `TOAST-0015` inherits a fourth call site or three.

4. ~~**Does a class default to a structural dump at all?**~~ **Decided 2026-08-17:
   `R { N = 5 }`**, type name plus fields. Accepts the disclosure surface in exchange for
   keeping the default useful for logging and debugging, and fixes a class being
   indistinguishable from a record.

5. ~~**How much of §3–§7 lands as one change?**~~ **Resolved by the implementation shape:**
   the renderer is built and tested *unused*, then the call sites flip in one revertible
   commit. The output changes land together, but nothing depends on the renderer until it
   is complete and pinned against this document.

**Still open: question 3 above** — whether serialisation is the same contract. It decides
whether `ToshEngine.Pipelines.cs:270` flips with the other three call sites, and it is
needed before stage 2, not before stage 1.
