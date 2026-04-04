# Language Surface Audit

This audit compares ToSh's current language surface to the local reference repos that are checked out in this workspace:

- ToSh: `/home/komrad/projects/tosh`
- PowerShell: `/home/komrad/projects/PowerShell`
- Nushell: `/home/komrad/projects/nushell`
- Lua: `/home/komrad/projects/lua`
- ZSH: `/home/komrad/projects/zsh`

I sampled the actual parser/token definitions from those repos, then compared them to ToSh's current parser, lexer, builtins, and CLI surface.

## Current ToSh Surface

### Reserved statement keywords

As of the current parser, ToSh has a deliberately small reserved set:

- `var`
- `alias`
- `using`
- `require`
- `func`
- `return`
- `throw`
- `break`
- `continue`
- `if`
- `else`
- `for`
- `in`
- `while`
- `until`
- `try`
- `catch`
- `finally`
- `switch`
- `case`
- `default`

These are parser-level constructs, not ordinary commands.

### Language forms and special identifiers

These are not all reserved words in the same way, but they are language-level forms:

- `new`
- `nameof`
- `_` for the current pipeline item
- `$name` for variables
- `$tosh.Last.Result` for the most recent successful statement result
- `$(...)`, `$((...))`, `${...}`, `$"..."`, `$'...'`
- array literals: `[ ... ]`
- record literals: `{ Name = value }`
- ranges: `1..10`

### Operators currently recognized by evaluation

- arithmetic: `+`, `-`, `*`, `/`, `%`
- comparison: `==`, `!=`, `=~`, `!~`, `>`, `>=`, `<`, `<=`, `in`, `not-in`
- logical word operators: `and`, `or`, `not`
- text/type operators: `contains`, `starts-with`, `ends-with`, `is`, `is-not`
- assignment: `=`, `+=`, `-=`, `*=`, `/=`, `%=`, `??=`

### Tokens currently lexed beyond the core language

The lexer already recognizes more shell-like punctuation than the parser/runtime fully use today:

- `&`
- `>`
- `>>`
- `<`
- `<<<`
- `<(`
- `>(`
- `..`

That means ToSh is already carrying some future shell syntax in the lexer even when the parser/runtime story is incomplete.

### Builtin command families

ToSh already has a broad builtin surface. The largest families are:

- shell/session: `help`, `apropos`, `history`, `view`, `exit`, `clear`
- text/output: `echo`, `write`, `writeline`, `split`, `join-lines`, `replace`, `match`, `template`, `hash`
- filesystem: `ls`, `pwd`, `cd`, `stat`, `find`, `mkdir`, `touch`, `rm`, `cp`, `mv`, `chmod`, `chown`, `ln`, `readlink`, `realpath`, `dirname`, `basename`
- system/process: `uname`, `hostname`, `whoami`, `id`, `ping`, `ps`, `env`, `which`, `whence`, `free`, `uptime`, `df`, `du`
- data conversion: `from-json`, `from-csv`, `from-tsv`, `from-xml`, `to-json`, `to-csv`, `parse`
- pipeline/data shaping: `get`, `rename`, `inspect`, `where`, `each`, `first`, `last`, `skip`, `sort`, `sort-by`, `reverse`, `count`, `flatten`, `distinct`, `group-by`, `take-while`, `skip-while`, `tee`, `sum`, `average`, `avg`, `min`, `max`
- CLR/object interop: `type-of`, `describe-type`, `members`, `methods`, `constructors`, `types`, `load-assembly`, `cast`, `new`, `call`, `get-prop`, `set-prop`, `call-method`, `clone`

## Reference Language Snapshots

### Lua

Lua's parser keeps the reserved set small and expressive. Its core keywords are things like:

- `and`, `break`, `do`, `else`, `elseif`, `end`, `false`, `for`, `function`
- `if`, `in`, `local`, `nil`, `not`, `or`, `repeat`, `return`, `then`, `true`, `until`, `while`

Its operator set is compact and language-first: `..`, `...`, `==`, `~=`, `<=`, `>=`, `//`, `<<`, `>>`, plus word operators like `and`, `or`, `not`.

Design lesson for ToSh:

- keep the reserved set small
- let keyword count stay closer to Lua than C# or PowerShell
- prefer a lightweight scripting feel over a very heavy declaration surface

### ZSH

ZSH keeps the reserved-word set relatively small and pushes lots of behavior into:

- builtins
- shell expansions
- redirections
- condition syntax
- parameter expansion

Its reserved words include:

- `if`, `then`, `else`, `elif`, `fi`
- `for`, `foreach`, `while`, `until`, `repeat`, `select`
- `case`, `esac`, `do`, `done`
- `function`, `time`, `coproc`
- special condition/shell markers like `!`, `[[`

Design lesson for ToSh:

- shell feel comes from operators, pipes, redirection, and expansion as much as it does from keywords
- aliases/functions/builtins should stay important, not everything needs to become syntax

### Nushell

Nushell is much more command-driven than traditional shells. Its parser is type-directed and many "language" constructs are implemented as keyword-like commands with parser shapes.

Important ToSh-relevant patterns:

- `where` is a keyword-style command with a row-condition shape
- closures and row conditions are central
- `if` uses keyword signposts like `else`
- `for` and `while` are present, but still live in a highly command-shaped language

Design lesson for ToSh:

- commands like `where`, `each`, `select`, and `group-by` should remain first-class
- ToSh does not need to turn every high-value pipeline concept into a parser keyword

### PowerShell

PowerShell has a very wide token and keyword surface. It includes:

- many statement keywords: `if`, `switch`, `foreach`, `for`, `while`, `do`, `function`, `filter`, `return`, `throw`, `try`, `catch`, `trap`, `class`, `enum`, `using`
- many operator tokens: `&&`, `||`, `!`, `::`, `?.`, `?`, `..`, assignment operators, format operators, redirection tokens
- distinct command mode and expression mode

It also reserves some things that are not fully implemented or not commonly used.

Design lesson for ToSh:

- borrow CLR fluency, not keyword bloat
- borrow object seriousness, not necessarily PowerShell's full reserved surface
- avoid carrying lots of "reserved but half-real" syntax long-term

### C#, Bash, and Nim

These were not sampled from local repos in this audit, but they are still useful design anchors:

- C#: strong type/member/import story, rich operators, large keyword surface
- Bash: shell ergonomics, redirection, process substitution, backgrounding, parameter expansion
- Nim: lightweight typed syntax, readable declarations, low punctuation noise

Design lesson for ToSh:

- prefer C# for CLR access, not for total parser complexity
- prefer Bash/ZSH for shell grammar expectations
- prefer Lua/Nim for lightweight script readability

## Findings

### 1. ToSh is already closest to the right overall direction

The current parser is much closer to "Lua + Nu + CLR shell" than to "PowerShell clone":

- reserved keyword set is still small
- data shaping is command-oriented
- `.NET` access is becoming fluent
- explicit `$variable` usage keeps scripts readable and shell-safe
- `_` and `$tosh.Last.Result` are a good split between item scope and statement scope

This is good. It should be preserved.

### 2. There are a few real surface inconsistencies today

#### `func` vs `def`

The parser uses `func`, but parts of the user-facing docs and banner/help text still mention `def`.

Recommendation:

- keep `func` as the primary surface
- remove stale `def` references from docs/help
- if `def` ever exists again, make it an explicit compatibility alias, not the advertised default

#### `using` is overloaded

Today `using` covers both:

- CLR namespace/type imports
- ToSh file/module loading

That is convenient short-term but muddy long-term.

Recommendation:

- keep `using` for CLR imports and aliases
- introduce `import` or `module` for ToSh files/modules
- optionally keep file-based `using` as transitional compatibility

#### Some command names and syntax names overlap awkwardly

Examples:

- `new` exists as both a language form and a command
- `call` exists as both a command and a fallback interop tool even though fluent member calls now exist
- `match` is currently a command name, but `match` is also a strong candidate for future pattern-matching syntax

Recommendation:

- keep expression/native syntax primary
- demote escape-hatch commands in docs/help
- consider renaming:
  - `new` command -> `new-object`
  - `call` command -> `invoke-member`
  - `match` command -> `match-text` if `match` becomes syntax later

#### The lexer and parser are out of sync on some symbols

ToSh currently lexes:

- `&`
- redirection operators
- process substitution tokens

But these are not yet a complete parser/runtime feature set.

Also, `and` works in predicates today, but `&&` currently does not behave as a first-class equivalent, even though that was part of the intended surface.

Recommendation:

- either fully implement tokenized shell syntax
- or stop tokenizing/highlighting unsupported forms until they are real

Partial syntax is worse than absent syntax once users rely on it.

#### Syntax highlighting is currently broader than the real keyword set

The highlighter still treats some command-like words as keywords.

Recommendation:

- keep real parser keywords highlighted as keywords
- highlight command-like language forms separately if desired
- highlight operator words as operators, not keywords

### 3. ToSh is missing a few obvious "finish the language" pieces

These are the most natural additions if we want a complete lightweight shell language:

- `until`
- `switch`
- `match`
- `try`
- `catch`
- `finally`
- `throw`
- `import`
- `module`
- `reload`

These fit the current direction much better than adding dozens more reserved words.

### 4. Operator depth is still thinner than the shell ambition

If ToSh wants real ZSH/Bash/Nu/PowerShell fluency, the operator story should eventually include:

- `&&`, `||`, `!`
- shell redirection and background execution forms

The following have now been implemented:

- `??` (null-coalescing)
- `?.` (null-safe member access)
- `=~`, `!~` (regex match operators)
- `in`, `not-in` (membership operators)
- `+=`, `-=`, `*=`, `/=`, `%=` (compound assignment)
- `??=` (null-coalescing assignment)

Semantic changes implemented during the deep audit:

- `==` no longer conflates equality with collection containment; use `in` for membership testing
- `+` is now symmetric for strings: either side being a string triggers concatenation
- truthiness is now broader: `null`, `0`, `""`, `false`, and empty collections are falsy; everything else is truthy
- forward type references work through two-pass registration (class/record/enum names visible before body is evaluated)
- circular `require` is detected and raises an error
- `global var` inside a module scope redirects to module exports (requires module-qualified access)
- defining a function with the same name as a built-in emits a warning
- redeclaring `_` when a binding already exists emits a warning
- `$obj..method()` (accidental double-dot) gets a parser diagnostic
- event handler captured scopes are now checked during `forget` disposal

Recommendation:

- finish the operator set in layers
- document precedence explicitly
- keep word operators and symbolic operators equivalent where possible

## Recommended Target Surface

### Keep the reserved keyword set small

Recommended core statement keywords:

- `var`
- `func`
- `alias`
- `using`
- `import`
- `return`
- `break`
- `continue`
- `if`
- `else`
- `for`
- `in`
- `while`
- `until`
- `switch`
- `match`
- `try`
- `catch`
- `finally`
- `throw`

Notes:

- `set` should be reconsidered. It is a friendly alias today, but shell users often expect `set` to manage shell state/options rather than declare variables.
- `where`, `each`, `select`, `group-by`, `first`, `last`, and similar filters should stay commands, not parser keywords.

### Keep the explicit sigil model

Recommended special identifiers:

- `$name` for variables
- `_` for the current pipeline item
- `$tosh.Last.Result` for the previous successful statement result

Likely future additions:

- `$in`
- `$error`
- `$exit`
- `$cwd`

This is one of the cleanest parts of the current design. It should stay explicit.

### Make shell syntax either real or absent

Recommended rule:

- if the lexer highlights or tokenizes a shell operator, the parser/runtime should support it
- otherwise remove it from the surfaced grammar for now

That applies especially to:

- `&`
- `>`, `>>`, `<`
- `<(`, `>(`
- `&&`, `||`, `!`

### Keep Unix names, but expose ToSh-native names too

This is already happening in some places:

- `df` and `mounts`
- `du`, `usage`, and `disk-usage`
- `sort` and `sort-by`

That is a good pattern. ToSh should keep familiar Unix names as aliases, while help/docs can gradually prefer clearer object-native names.

## Suggested Implementation Order

### P0: fix consistency

- clean up stale `def` references
- separate real keywords from command-like words in syntax highlighting/docs
- document the actual operator set
- decide whether `set` remains a parser keyword alias

### P1: finish the grammar boundaries

- add `import` / `module`
- stop overloading `using` for files
- either implement or remove half-surfaced shell tokens
- make `&&`, `||`, and `!` truly equivalent to `and`, `or`, and `not`

### P2: finish control-flow basics

- add `until`
- add `switch` or `match`
- add `try` / `catch` / `finally` / `throw`

### P3: finish shell operators

- background execution
- redirection
- process substitution
- richer assignment and null-handling operators

## Bottom Line

ToSh should stay closer to:

- Lua for keyword count and script feel
- ZSH/Bash for shell operators and expectations
- Nushell for display and data-shaping ergonomics
- PowerShell for CLR fluency and object seriousness

It should not copy any one of those languages wholesale.

The strongest immediate path is:

1. keep the reserved set small
2. split `using` from `import`
3. align docs/highlighting with the real parser
4. finish the operator and shell-token story
5. then grow control flow in a deliberate way
