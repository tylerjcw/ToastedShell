# TOAST-0087 step 1 — builtin side-effect metadata coverage audit

Dated 2026-08-28. The item's own ordering note is the reason this comes first:

> Enforcement before coverage would create a sandbox whose omissions look like safety.

This measures what the builtin metadata actually says today, so the size of the normalization
step is known before any of it is enforced.

## Method

Every class deriving from `ShellCommand` in `src/Toast.Stdlib` and `src/Tosh.Stdlib`
(excluding `obj/` and `bin/`), cross-referenced against the CLR APIs its own source file calls.
Script: `scratchpad/effect-audit.sh`.

**The observed column is a floor, not a ceiling.** Commands routinely delegate — `read-file`
performs its read through `FileIoUtilities.ReadAllTextAsync`, not `File.ReadAllText` — so a
source-local scan undercounts. Every number below understates the gap.

## Headline

| | count | share |
|---|---|---|
| command classes | 252 | — |
| declare `[CommandSideEffects]` | 15 | 6% |
| declaration consistent with their own source | 8 | **3%** |
| perform observable effects, declare nothing | 52 | 21% |
| declare `[CommandPermission]` | **0** | 0% |

161 of the commands live in `Toast.Stdlib`, 91 in `Tosh.Stdlib`.

## Finding 1 — the declared set is not trustworthy

Seven of the fifteen commands that declare effects declare too few:

| command | declares | also observed |
|---|---|---|
| `MakeDirectoryCommand` | `fs.write` | `fs.read` |
| `MoveItemCommand` | `fs.write` | `fs.read` |
| `RemoveItemCommand` | `fs.write` | `fs.read` |
| `TouchCommand` | `fs.write` | `fs.read` |
| `FormatCommand` | `fs.write` | `fs.read` |
| `CopyItemCommand` | `fs.read fs.write` | `native` |
| `HttpCommand` | `network` | `fs.read fs.write env.read` |

`HttpCommand` is the one to look at hardest: a command whose declaration says "network" also
writes files and reads the environment. A capability boundary trusting that declaration would
grant `network` and silently permit a file write.

So the metadata is not merely sparse — where it exists it is close to a coin flip. Only eight
commands in the whole shell carry a declaration consistent with what their source does.

## Finding 2 — the core file commands declare nothing

The commands a user would name first if asked which ones touch the disk:

`read-file`, `write-file`, `read-bytes`, `write-bytes`, `read-lines`, `append-file`,
`head`, `tail`, `stat`, `chmod`, `chown`, `df`, `du`, `tee`, `hash`, `realpath`, `readlink`,
`temporary-file`, `make-temp-directory`, `change-directory`, `print-working-directory`

None of them declares an effect. The fifteen that do declare look arbitrary next to this list —
`cat` and `grep` are annotated while `read-file` is not.

## Finding 3 — five effect kinds cannot be expressed at all

`CommandSideEffectsMetadata` is four booleans: `ReadsFiles`, `WritesFiles`, `Network`,
`SpawnsProcess`. The item asks for at least ten effects. Commands observed performing effects
with no vocabulary to declare them:

| effect | commands | examples |
|---|---|---|
| `native` | 8 | `native-read`, `native-write`, `native-sizeof`, `native-offsetof`, `ulimit`, `umask`, `link`, `copy-item` |
| `env.read` | 4 | `environment`, `vars`, `edit`, `http` |
| `terminal` | 2 | `read-line`, `tui` |
| `env.write` | 1 | `environment` |
| `clr` | — | `load-assembly` (loads arbitrary assemblies; scanned only as `fs.read`) |

A diligent author writing `native-write` today has nothing true to say. This is a gap in the
model, not author neglect, and it is the part of the normalization step that cannot be done by
annotating harder.

`load-assembly` deserves separate mention: it loads a CLR assembly into the process and is the
single widest authority any builtin holds. Its declared metadata is empty.

## Finding 4 — 14 commands shell out and none say so

`systemctl`, `journalctl`, `loginctl`, `hostnamectl`, `networkctl`, `lsblk`, `lscpu`, `lsipc`,
`findmnt`, `ip`, `tree`, `time`, and others start processes. `KillCommand` is the only command
in the shell declaring `SpawnsProcess = true`.

For a capability boundary this is the most consequential gap: spawning a process escapes every
other effect category at once.

## Finding 5 — nothing consumes the metadata

`SideEffects` is read in exactly three places, all of them descriptive: help text, MCP command
metadata, and LSP hover. No evaluator, type checker, or sandbox path reads it. That is the
item's premise, and it holds — but the audit adds a sharper point: because nothing consumes it,
nothing has ever pressured it toward correctness, which is why Finding 1 looks the way it does.

`[CommandPermission]` is worse than unconsumed. The attribute exists, `CommandMetadata` carries a
`Permissions` list, and `ToshLanguageFeatures` renders **Permissions:** in hover — but no command
anywhere applies the attribute. The feature is fully plumbed and entirely empty. It should either
be populated by the registry work or deleted; leaving a rendered-but-never-set permission field
is how a reader concludes a command needs no permissions.

## Finding 6 — absence is indistinguishable from purity

`ShellCommand.Describe` leaves `sideEffects` null when the attribute is absent:

```csharp
CommandSideEffectsMetadata? sideEffects = null;
var seAttr = type.GetCustomAttribute<CommandSideEffectsAttribute>();
if (seAttr is not null)
    sideEffects = new(seAttr.ReadsFiles, seAttr.WritesFiles, seAttr.Network, seAttr.SpawnsProcess);
```

Null means "nobody said", and 94% of commands are null. Any consumer that reads null as "no
effects" — the natural reading, and the one a `pure func` check would want — would conclude
that `write-file` is pure. The registry needs a third state, or a default of "unknown, treat as
unrestricted", so the safe reading is the one you get for free.

## What this implies for the ordering

The item's step 1 is "normalize and audit builtin metadata". The audit says normalization is
not an annotation pass over a mostly-correct corpus; it is closer to a from-scratch
classification of 252 commands against a vocabulary that does not exist yet. Concretely, in
order:

1. **Design the effect vocabulary**, including how it composes with the RFC's `core` / `os` /
   `native` / `unsafe` / `clr` target capabilities. Needs a decision (see below).
2. **Make absence explicit** — an unannotated command must not read as pure.
3. **Classify all 252 commands** against the new vocabulary.
4. **Add a guard test** asserting every `ShellCommand` carries an effect declaration, so the
   corpus cannot regress to 6% again. Without this, step 3 decays.
5. Only then: inference, `pure func`, and capability enforcement.

Steps 2 and 4 are what stop this from being a one-time cleanup.

## Open decision for the vocabulary

Whether effects are a **closed enum** (`fs.read`, `fs.write`, …) or an **open hierarchical
namespace** (`fs.read`, `fs.read.temp`, `net.http`, …). The closed set is checkable and
exhaustively matchable; the open one lets `native` and `clr` carry detail the shell cannot
anticipate and matches the free-form intent behind `[CommandPermission]`. The item's phrase
"aliases/aggregation may present these more simply to users" suggests a closed core with
presentation grouping, but it is not settled, and it determines whether step 3 is mechanical.
