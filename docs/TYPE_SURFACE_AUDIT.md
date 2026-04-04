# ToSh Type Surface Audit

## Purpose

This audit answers a narrow question:

> What value and type shapes are first-class in ToSh today, which ones are only available through CLR interop, and which ones still feel incomplete or ambiguous?

This is not a parser audit or command audit in general. It is specifically about:

- scalar types
- temporal and size values
- lists, arrays, dictionaries, and ranges
- anonymous records vs named records
- classes, enums, modules
- how this compares to NuShell, PowerShell, Lua, and Zsh expectations

## Current ToSh Surface

### Scalars

ToSh currently has good first-class surface support for:

- strings
- numbers
- booleans
- `null`
- `StorageSize`
- `TimeSpan`
- `DateTimeOffset`
- `TemporalAmount`

Relevant runtime/parser files:

- [ArgumentSyntax.cs](/home/komrad/projects/tosh/src/Tosh.Language/Parsing/ArgumentSyntax.cs)
- [ToshParser.cs](/home/komrad/projects/tosh/src/Tosh.Language/Parsing/ToshParser.cs)
- [TemporalParser.cs](/home/komrad/projects/tosh/src/Tosh.Core/TemporalParser.cs)
- [TypeConversion.cs](/home/komrad/projects/tosh/src/Tosh.Core/TypeConversion.cs)
- [DotNetTypeResolver.cs](/home/komrad/projects/tosh/src/Tosh.Core/DotNetTypeResolver.cs)

Observations:

- `DateTimeOffset` is effectively the shell's default instant type.
- `TimeSpan` works for fixed durations.
- `TemporalAmount` covers mixed calendar-style durations such as `1y2mo`.
- `StorageSize` is already a strong shell-native type and is one of the cleanest parts of the surface.

### CLR Type Aliases

The built-in alias surface is still fairly small:

- `bool`, `byte`, `char`
- `datetime`, `decimal`, `double`
- `duration`, `dynamic`
- `file`, `float`, `guid`
- `int`, `long`, `object`, `short`, `string`
- `temporalamount`, `timespan`, `uri`

See [DotNetTypeResolver.cs](/home/komrad/projects/tosh/src/Tosh.Core/DotNetTypeResolver.cs).

Observations:

- ToSh now has shell-facing aliases for `list`, `array`, `dict`, `map`, `set`, `hashtable`, `tuple`, and `table`.
- Generic CLR type syntax is still not yet a polished first-class part of the language surface.

## Collections

### `[]` Array Literals

`[]` now evaluates to an array value.

Evidence:

- [ArgumentSyntax.cs](/home/komrad/projects/tosh/src/Tosh.Language/Parsing/ArgumentSyntax.cs): `ArrayLiteralArgumentSyntax`
- [ToshEngine.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshEngine.cs): array literals return `items.ToArray()`

This is the current behavior:

```tosh
var values = [1, 2, 3]
```

The runtime object is an array.

### Mutable Lists

Mutable lists still exist, but they no longer share `[]` syntax.

Use:

- `new list(...)`
- CLR interop
- conversion/binding when a `List<T>` is expected

Examples:

- `new list(1, 2, 3)`
- `new list([1, 2, 3])`

Evidence:

- [TypeConversion.cs](/home/komrad/projects/tosh/src/Tosh.Core/TypeConversion.cs)
- [StructuredDataCommandTests.cs](/home/komrad/projects/tosh/tests/Tosh.Tests/StructuredDataCommandTests.cs)
- [PipelineDataCommandTests.cs](/home/komrad/projects/tosh/tests/Tosh.Tests/PipelineDataCommandTests.cs)

Important distinction:

- `[]` means array
- `new list(...)` means mutable list

### Ranges

Ranges are implemented and fairly coherent:

- syntax: `1..5` and `1..2..9`
- runtime type: `ToshRange`
- behavior: ranges expand when appropriate in pipelines and slicing

Evidence:

- [ToshRange.cs](/home/komrad/projects/tosh/src/Tosh.Core/ToshRange.cs)
- [ToshParser.cs](/home/komrad/projects/tosh/src/Tosh.Language/Parsing/ToshParser.cs)
- [ToshEngine.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshEngine.cs)
- [GetCommand.cs](/home/komrad/projects/tosh/src/Tosh.Core/Commands/GetCommand.cs)

This part of the surface is closer to NuShell than to PowerShell or Zsh, and it fits ToSh well.

### Dictionaries / Maps

ToSh does not yet have a distinct first-class dictionary literal surface.

What exists today:

- dynamic record literals backed by `ExpandoObject`
- CLR dictionaries through interop
- conversions through serializer/object accessor utilities

Evidence:

- [ShellRecordUtilities.cs](/home/komrad/projects/tosh/src/Tosh.Core/ShellRecordUtilities.cs)
- [ToshEngine.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshEngine.cs)
- [ShellDataSerializer.cs](/home/komrad/projects/tosh/src/Tosh.Core/ShellDataSerializer.cs)

Important distinction:

- a record is not yet the same thing as a general dictionary
- record fields are name-oriented
- arbitrary non-identifier keys are not a polished first-class language concept yet

### Tuples

There is no first-class tuple literal or tuple pattern surface today.

CLR tuples can still flow through ToSh if constructed through interop, but ToSh has no dedicated tuple syntax.

### Sets

There is no first-class set literal or set type surface.

## Records And Objects

### Anonymous Tables

`{ ... }` currently creates an anonymous dynamic `table` backed by `ExpandoObject`.

Evidence:

- [ArgumentSyntax.cs](/home/komrad/projects/tosh/src/Tosh.Language/Parsing/ArgumentSyntax.cs): `RecordLiteralArgumentSyntax`
- [ToshEngine.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshEngine.cs): record literals allocate `ExpandoObject`
- [ShellRecordUtilities.cs](/home/komrad/projects/tosh/src/Tosh.Core/ShellRecordUtilities.cs)

This is already a real strength in ToSh.

The right way to think about it is:

- surface name: `table`
- backing runtime type: `ExpandoObject`

### Named Records

Named records are now their own type surface:

```tosh
record Item(name: string, quantity: int, category?: string = "Food")
```

Evidence:

- [ToshRecordDefinition.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshRecordDefinition.cs)
- [ToshRecordInstance.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshRecordInstance.cs)
- [StatementSyntax.cs](/home/komrad/projects/tosh/src/Tosh.Language/Parsing/StatementSyntax.cs)

Observations:

- named `record` and anonymous `{ ... }` now serve different purposes cleanly
- this is a good split and should stay

Recommended terminology:

- `{ ... }` -> anonymous `table`
- `record Name(...)` -> named record

### Projections

Projection wrappers have now been retired in favor of shell-native `table` and dictionary values.

## Named Types

### Classes

Classes exist and are becoming first-class:

- constructors
- instance/static methods
- properties
- `shy` members
- shell-aware reflection and LSP support

Relevant files:

- [ToshClassDefinition.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshClassDefinition.cs)
- [ToshClassInstance.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshClassInstance.cs)
- [ToshEngine.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshEngine.cs)

Current state:

- strong enough for real experimentation
- still not fully aligned with every CLR type expectation

### Enums

Enums are now present and useful:

- named enum type
- optional underlying numeric type
- symbolic member access

Relevant files:

- [ToshEnumDefinition.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshEnumDefinition.cs)
- [ToshEnumValue.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshEnumValue.cs)

### Modules

Modules exist and provide organization, namespace-like grouping, and scoped `require`.

Relevant files:

- [ToshModuleObject.cs](/home/komrad/projects/tosh/src/Tosh.Language/ToshModuleObject.cs)
- [ModuleExportTable.cs](/home/komrad/projects/tosh/src/Tosh.Language/ModuleExportTable.cs)

This is a better fit for ToSh than adding a separate `namespace` feature right now.

## Comparison Snapshot

### NuShell

NuShell has a very explicit structured surface:

- list
- record
- table
- range

Its strength is that these concepts are named and visually coherent.

ToSh currently matches Nu well on:

- ranges
- records as displayable data objects
- pipelines that operate on structured values

ToSh still lags Nu on:

- explicit collection-shape clarity
- clear distinction between list, record, and table-like values in the language surface

Reference signals:

- [nushell README.md](/home/komrad/projects/nushell/README.md)

### PowerShell

PowerShell exposes:

- arrays
- hashtables
- PSCustomObject-style records
- classes
- enums
- deep CLR interop

ToSh is philosophically closer to PowerShell for runtime values, but it still lacks PowerShell's obvious built-in collection/dictionary surface.

ToSh is already stronger than PowerShell in some areas:

- record/list display aesthetics
- explicit non-flattening object-pipeline direction

ToSh still lags PowerShell on:

- collection and dictionary ergonomics
- mature type construction surface

### Lua

Lua's key lesson is simplicity:

- one table type covers list-like and map-like cases

ToSh should not copy that literally, because it wants typed CLR values and clearer shell display. But Lua is a strong reminder not to over-fragment the user model.

Reference signals:

- Lua internals and standard surface heavily center around tables
- [lua source tree](/home/komrad/projects/lua)

### Zsh

Zsh strongly exposes:

- arrays
- associative arrays
- typed scalars through shell declarations such as integer/float/typeset

ToSh does not need to imitate Zsh syntax, but Zsh is a useful reminder that shells feel more complete once basic collection shapes are obvious and scriptable.

Reference signals:

- [zsh README](/home/komrad/projects/zsh/README)
- [zsh docs](/home/komrad/projects/zsh/Doc)

## Main Gaps

### 1. Array vs Mutable List Is Much Clearer

Current state:

- `[]` creates arrays
- `new list(...)` creates mutable lists
- CLR arrays still interoperate naturally

The remaining work here is ergonomics, not ambiguity.

### 2. Dictionary / Map Surface Is Missing

Anonymous tables are useful, but they are not a complete replacement for dictionaries.

Needed eventually:

- a distinct dictionary/map type surface
- or an explicitly documented rule that tables are the default string-keyed map surface

My recommendation:

- do not rush a separate literal immediately
- first formalize tables as the shell's default string-keyed map-like shape
- then decide whether explicit dictionaries still need a separate literal

### 3. Projection Wrappers Are No Longer Part Of The Runtime Surface

The user-facing row/object story is now:

- anonymous `table`
- named `record`
- CLR dictionary-like values

### 4. Generic CLR Collection Surface Is Still Shallow

ToSh can interoperate with CLR generic collections, but the language surface for them is not yet polished.

Examples of missing polish:

- no first-class generic construction ergonomics
- no strong story yet for when users should choose CLR collection types over ToSh literals

### 5. Tuple And Set Surfaces Do Not Exist Yet

This is acceptable for now, but it should be acknowledged as intentionally missing.

## Recommended Direction

### Keep

- anonymous tables backed by `ExpandoObject`
- named `record`
- `class`
- `enum`
- `module`
- `ToshRange`
- `StorageSize`
- temporal literals with `TimeSpan` + `TemporalAmount`

### Clarify Immediately

- `[]` means array
- `{ ... }` means anonymous `table`
- named `record` is separate from anonymous `table`
- mutable lists come from `new list(...)`

### Add Next

1. Clear dictionary/map story
2. Better generic CLR collection ergonomics
3. A short “type surface” help topic and docs examples

## Recommended Implementation Order

1. Document the current truth in user-facing docs:
   - `[]` is array
   - `{ ... }` is anonymous `table`
   - named `record` is typed shell data
2. Decide whether `table` is enough for maps, or whether a separate dictionary literal is still needed
3. Improve generic/container construction ergonomics
4. Add more explicit help/examples for common CLR collection types

## Bottom Line

ToSh's type surface is already strong in the following areas:

- shell-native typed values
- dynamic records
- named records
- enums
- classes
- modules
- temporal and size values

The main thing making it feel unfinished is not that there are no types.

It is that the collection story is still slightly fuzzy:

- list vs array
- record vs dictionary
- projection vs record

If we tighten those three boundaries, ToSh will feel much more deliberate very quickly.
