# Unix Command Audit

This audit covers ToSh built-ins that intentionally shadow, replace, or wrap familiar Unix commands such as `ls`, `ps`, `du`, `df`, `stat`, `find`, `cat`, `grep`, `cp`, `mv`, and related tools.

The goal is not byte-for-byte GNU/Coreutils/procps parity.

The goal is:

- preserve the familiar command names and the most useful flags
- keep output strongly object-oriented
- expose at least the same information as the original command where it matters
- make richer information available without collapsing back to raw text
- add a universal display-selection layer so users can choose which properties are shown per command

## Core Principles

1. Object output stays primary.
   `ls`, `ps`, `df`, `du`, `stat`, `ip`, and similar commands should keep returning typed objects, not terminal-shaped strings.

2. Flag parity is selective, not literalist.
   We should mirror original short and long flags where the semantics map cleanly into ToSh.

3. Text-format-only GNU features should become object/display features where possible.
   Example: `df --output` should map to typed property selection, not to manual string formatting.

4. Display control must not mutate the pipeline objects.
   A user should be able to say `ls --show Name,FullName,Size` and still pipe full `FileSystemEntry` objects into later commands.

5. We should prefer “same information, better structure” over perfect textual compatibility.

## Parity Levels

- `Strong`: the command already covers the common daily surface and produces richer typed output
- `Partial`: useful core behavior exists, but several common flags or behaviors are still missing
- `Minimal`: the command name exists and the core action works, but parity is still far from daily-driver expectations

## Universal Output-Selection Proposal

This should be the shared design for object-returning Unix-style commands.

Status:

- first pass is now implemented for `ls`, `ps`, `df`, and `du`
- `ps -o ...` maps onto the shared selection layer
- `df --output ...` maps onto the shared selection layer
- selections are display-only and do not project away the underlying objects
- `ls` now has a real first parity slice: `-aAldRFihrSt`, `--sort`, `--time`, and `--group-directories-first`
- `ps` now has a real first parity slice: `-e`, `-A`, `-f`, `-p`, `-u`, `-U`, `-t`, `--ppid`, `--sort`, and `-o`

### Canonical Flags

- `--show <prop[,prop...]>`
- `--hide <prop[,prop...]>`
- `--show-all`

### Semantics

- `--show` selects which properties are rendered
- `--hide` removes properties from the rendered output
- `--show-all` includes non-default properties that are available on the underlying object type
- these flags affect display only
- the objects flowing through the pipeline remain unchanged

### Important Constraint

This layer must not be implemented as a pre-`get` projection.

It should be implemented as a display override attached to the command result set, so:

```tosh
ls --show Name,FullName
ls --show Name,FullName | get FullName
```

still operates on full `FileSystemEntry` objects.

### Command-Specific Aliases

Where a standard Unix command already has a strong field-selection concept, we should map it onto the same mechanism:

- `ps -o ...` -> alias for `--show`
- `df --output=...` -> alias for `--show`

For commands that do not already have a standard field-selection flag, we should keep the universal long names:

- `ls --show ...`
- `du --show ...`
- `stat --show ...`

### Related Follow-Up

`get` should ultimately be able to access all public shell-visible members, not only the default visible display columns.

That is a separate but related cleanup.

## Audit Summary

## High-Priority Commands

These are the commands most likely to define whether ToSh feels like a serious daily shell.

| Command | Current Surface | Output | Parity | Biggest Gaps |
| --- | --- | --- | --- | --- |
| `ls` | `-a`, `-A`, `-l`, `-d`, `-R`, `-F`, `-i`, `-r`, `-S`, `-t`, `--sort`, `--time`, `--group-directories-first` | `FileSystemEntry` | `Strong` | still missing symlink dereference controls and a few GNU-specific formatting/indicator variants |
| `ps` | positional filters, `-e`, `-A`, `-f`, `-p`, `-u`, `-U`, `-t`, `--ppid`, `--sort`, `-o` | `ProcessInfo` | `Partial` | still missing tree views, no-header/raw modes, and broader selector families |
| `du` | `-a`, `-s`, `-d/--max-depth`, `-c`, `-x`, `--time` | `PathUsageInfo` | `Partial` | still missing apparent-size, inode counts, threshold/exclude filters, and broader GNU switches |
| `df` | path filters, `-h`, `-T`, `-l`, `-t`, `-x`, `--total`, `--output` | `FileSystemUsageInfo` | `Partial` | still missing inode view and a few GNU-specific reporting modes |
| `stat` | `-L`, `-f`, display-selection flags | `FileSystemEntry` / `FileSystemUsageInfo` | `Partial` | still missing terse/custom format strings and broader GNU compatibility switches |
| `find` | `-name`, `-regex`, `-iregex`, `-type`, `-maxdepth`, `-mindepth` | `FileSystemEntry` | `Partial` | missing `-iname`, `-path`, size/time/perm/user/group tests, xdev/prune/exec model |
| `grep` | `-i`, `-m`, `-s`, `-x`, `--explicit-capture`, `-v`, `-F`, `-n` | `GrepMatchInfo` | `Partial` | missing count/list/context/recursive/only-matching/word matching |
| `cat` | files, explicit `-`, piped text/file input, `-n`, `-b`, `-s` | `ShellTextLine` / numbered record rows | `Partial` | still missing show-tabs/show-ends/show-nonprinting and some stricter stdin edge-case parity |
| `head` | `-n` | objects or `ShellTextLine` | `Partial` | missing byte mode, quiet/verbose, zero-terminated |
| `tail` | `-n` | objects or `ShellTextLine` | `Partial` | missing byte mode, follow mode, quiet/verbose, zero-terminated |
| `wc` | `-l`, `-w`, `-c`, `-m`, `-L`, typed totals | `TextStatistics` | `Partial` | still missing closer byte/line parity edge cases, byte-vs-char locale nuances, and some GNU formatting modes |

## Filesystem Mutators

These matter because users expect them to be trustworthy and unsurprising.

| Command | Current Surface | Output | Parity | Biggest Gaps |
| --- | --- | --- | --- | --- |
| `mkdir` | `-p` | `DirectoryInfo` | `Partial` | missing `-m`, `-v` |
| `touch` | `-a`, `-m`, `-c`, `-d`, `-r` | `FileInfo` / `DirectoryInfo` | `Partial` | still missing broader timestamp syntax parity and a few GNU compatibility switches |
| `rm` | `-r`, `-f` | removed path object | `Partial` | missing directory-only, interactive/no-clobber style safety, verbose/preserve-root semantics |
| `cp` | `-r` | copied path object | `Partial` | missing overwrite/update/no-clobber/target-directory/preserve/link behavior |
| `mv` | default overwrite, `-n`, `-u`, `-f`, `-t`, `-T` | moved path object | `Partial` | still missing directory-overwrite parity, backup/verbose/interactive modes, and some GNU edge-case behavior |
| `chmod` | `-R` | `FileSystemEntry` | `Partial` | strong core, but missing symlink traversal policy and broader GNU compatibility flags |
| `chown` | `-R` | `FileSystemEntry` | `Partial` | strong core, but missing symlink traversal policy and broader GNU compatibility flags |
| `ln` | `-s`, `-f` | `FileSystemEntry` | `Partial` | missing relative-symlink helpers, backup/no-dereference/target-directory behavior |

## Path and Identity Utilities

These are smaller, but users still expect familiar flag behavior.

| Command | Current Surface | Output | Parity | Biggest Gaps |
| --- | --- | --- | --- | --- |
| `pwd` | none | `DirectoryInfo` | `Strong` | optional raw path mode may still be useful |
| `cd` | positional path | session mutation | `Strong` | mostly fine for ToSh’s object model |
| `readlink` | `-f` | `string` | `Partial` | missing verbosity/canonicalization variants |
| `realpath` | no flags | `string` | `Partial` | missing `--relative-to`, `--canonicalize-missing`, etc. |
| `basename` | positional path + suffix | `string` | `Partial` | missing multi-name GNU forms/options |
| `dirname` | positional path(s) | `string` | `Partial` | missing zero-terminated mode |
| `which` / `whence` | names only | `CommandResolution` | `Partial` | missing silent/all/alias/function distinctions found in shells |
| `env` | query names, `name=value`, `-u`, nested command execution | `EnvironmentVariableEntry` / nested command output | `Partial` | still missing `-i`/ignore-environment style behavior and a few GNU compatibility flags |
| `uname` | `-a -s -n -r -v -m -o` | `UnameInfo` / scalar / record | `Strong` | good fit already |
| `id` | `-u -g -G -n` | identity object or scalar(s) | `Partial` | missing user lookup argument and some standard selectors |
| `hostname` | none | `string` | `Minimal` | missing set/short/domain/fqdn-style options |
| `whoami` | none | identity/principal string | `Strong` | simple and sufficient |
| `free` | none | typed memory info | `Partial` | missing byte/unit selectors and totals breakdown controls |
| `uptime` | none | typed uptime info | `Partial` | missing pretty/raw toggles and user count compatibility |

## Text Utilities

| Command | Current Surface | Output | Parity | Biggest Gaps |
| --- | --- | --- | --- | --- |
| `uniq` | `-c`, `-i` | input items or count record | `Partial` | missing duplicate-only/unique-only/field-skip/char-skip/width |
| `cut` | `-f`, `-d`, `-c` | `ShellTextLine` | `Partial` | missing byte mode, complement, output delimiter, only-delimited behavior |
| `tr` | `-d` | `ShellTextLine` | `Minimal` | missing squeeze, complement, classes, truncation, escape handling |

## Network / Process Wrappers

| Command | Current Surface | Output | Parity | Biggest Gaps |
| --- | --- | --- | --- | --- |
| `ping` | `-c`, `-W` | `PingReplyInfo` | `Partial` | missing packet size, interval, TTL, quiet/summary controls, IPv4/IPv6 selectors |
| `ip` | structured `addr`, `link`, and `route` via `ip -j`, pass-through otherwise | `IpInterfaceInfo` / `IpRouteInfo` / external fallback | `Partial` | still needs deeper subcommand coverage, more column aliases, and a unified story for the broader `ip` family |
| `lsblk` | structured JSON-backed tree/list/block filters plus `-o` / `-O` | `BlockDeviceInfo` | `Partial` | needs deeper parity for raw/no-headings/pairs/filter expressions and more util-linux column families |
| `lscpu` | structured JSON-backed summary, extended topology, and cache views | `CpuInfo` / `CpuTopologyInfo` / `CpuCacheInfo` | `Partial` | needs deeper parity for parse/raw/sysroot modes, better summary sub-grouping, and more `--extended` / `--caches` column families |
| `lsfd` | structured JSON-backed descriptor rows plus typed summary counters | `FileDescriptorInfo` / `SystemCounterInfo` | `Partial` | needs deeper parity for raw/no-headings/filter expressions, custom counters, and more socket/process-specific display presets |
| `lsipc` | structured JSON-backed IPC resource and limit rows | `ExpandoObject` structured records | `Partial` | needs deeper parity for export/raw/shell output modes, richer column catalogs, and maybe dedicated reusable IPC/domain objects if more commands start sharing that surface |
| `findmnt` | structured JSON-backed mount-tree/list browsing plus `-o` / `--output-all` | `MountInfo` | `Partial` | needs deeper parity for poll/verify/raw/no-headings/pairs and more mount-table source modes |

## Command-by-Command Findings

### `ls`

Current ToSh surface:

- `-a`
- `-A`
- `-l`
- `-d`
- `-R`
- `-F`
- `-i`
- `-r`
- `-S`
- `-t`
- `--sort`
- `--time`
- `--group-directories-first`
- returns `FileSystemEntry`

What is good:

- output is already typed
- hidden-file behavior exists
- long display intent exists
- common daily `ls` sort and traversal controls now map cleanly onto typed `FileSystemEntry` output
- `--time` changes both display and time-based sort semantics without collapsing back to text
- command-specific display flags can now surface fields like `FullName`, `Created`, `Accessed`, `Owner`, and `Inode`

What is missing for daily use:

- symlink dereference controls
- GNU-style color/indicator variants beyond ToSh's built-in rich display
- optional pipeline-path consumption if we decide `ls` should participate in that object-shell pattern

Recommendation:

- keep `ls` object-first, but treat this first parity slice as the new baseline
- add symlink dereference controls next if daily-driver testing shows a need
- consider a path-input pipeline mode separately from GNU parity

### `ps`

Current ToSh surface:

- optional positional name/id filtering
- `-e` / `-A`
- `-f`
- `-p`
- `-u` / `-U`
- `-t`
- `--ppid`
- `--sort`
- `-o`
- returns `ProcessInfo`

What is good:

- typed process objects are exactly the right direction
- common `ps` selectors now map onto typed `ProcessInfo` filtering instead of text scraping
- `-o` is now a real alias for the universal display-selection layer
- `-f` provides a fuller object display preset without changing the pipeline values

What is missing:

- long/full/tree modes
- headers/no-headers behavior
- additional selector families like session, group, command-line regex, and forest/tree traversal

Recommendation:

- `ps` should become the model for “object parity, not text parity”
- keep typed objects and continue adding selector families instead of raw text formatting clones
- add tree/forest-style object views next if daily-driver testing calls for them

### `du`

Current ToSh surface:

- `-a`
- `-s`
- `-d` / `--max-depth`
- `-c`
- `-x`
- `--time`
- returns typed usage objects

What is good:

- object output is already better than plain text
- recursive traversal exists
- totals now stay typed instead of getting flattened into summary text
- `-x` maps cleanly onto the shell's filesystem model
- `--time` adds structured timestamp metadata instead of raw suffix text

What is missing:

- `-h` / `--si`
- `--apparent-size`
- `--inodes`
- `--exclude`
- `--threshold`
- time-related options

Recommendation:

- keep object output and treat this first parity slice as the new baseline
- the next strong additions are apparent-size, inode counts, and exclusion/threshold filters
- consider `--show`/`--hide` for fields like `Depth`, `FullPath`, `IsDirectory`, `Size`

### `df`

Current ToSh surface:

- optional path filter only
- `-h`
- `-T`
- `-l`
- `-t`
- `-x`
- `--total`
- `--output`
- returns typed filesystem-usage objects

What is good:

- the underlying data is already object-friendly
- `--output` already maps to the shared display-selection system
- local/type filters now exist without breaking the typed output model
- totals stay typed as aggregate `FileSystemUsageInfo` rows

What is missing:

- `-i`
- broader GNU report shaping like `--sync`, `--portability`, or POSIX text formatting

Recommendation:

- preserve typed output and treat `df --output` as the gold standard for audit-compatible field selection
- inode metrics are the next most valuable parity step here

### `stat`

Current ToSh surface:

- `-L`
- `-f`
- display-only selection flags
- returns detailed `FileSystemEntry` or `FileSystemUsageInfo`

What is good:

- ToSh already has richer structured metadata than plain `stat` output
- `-L` and `-f` now map onto typed file-entry and filesystem-usage modes cleanly
- display selection lets users surface deeper metadata without losing the underlying object

What is missing:

- `-t`
- `-c` / `--format`
- `--printf`

Recommendation:

- keep the object record as default
- treat the typed file and filesystem modes as the primary interface
- only add GNU-style custom text formatting if we still want it after living with the object model longer

### `find`

Current ToSh surface:

- `-name`
- `-regex`
- `-iregex`
- `-type`
- `-maxdepth`
- `-mindepth`

What is good:

- typed `FileSystemEntry` output is right
- depth and type filters exist

What is missing:

- `-iname`
- `-path` / `-ipath`
- `-size`
- `-mtime` / `-atime` / `-ctime`
- `-user` / `-group`
- `-perm`
- `-empty`
- `-xdev`
- `-prune`
- `-delete`
- `-exec`
- expression combinators

Recommendation:

- do not try to clone the entire GNU expression language immediately
- instead add the most useful tests and keep complex post-filtering in the ToSh pipeline

### `grep`

Current ToSh surface:

- `-i`
- `-m`
- `-s`
- `-x`
- `--explicit-capture`
- `-v`
- `-F`
- `-n`
- typed `GrepMatchInfo` output

What is good:

- regex support is already stronger than most shells
- object output is a real upgrade

What is missing:

- `-c`
- `-l`
- `-L`
- `-o`
- `-w`
- `-r` / `-R`
- `-A` / `-B` / `-C`
- filename-only modes

Recommendation:

- after `ls` and `ps`, `grep` is a strong candidate for the next parity pass

## Proposed Implementation Order

### Phase 1: Daily-driver parity

- `ls`
- `ps`
- `df`
- `du`
- `stat`
- universal display-selection flags

### Phase 2: Search/text parity

- `find`
- `grep`
- `head`
- `tail`
- `wc`
- `uniq`
- `cut`
- `tr`

### Phase 3: Filesystem mutator hardening

- `cp`
- `mv`
- `rm`
- `mkdir`
- `touch`
- `chmod`
- `chown`
- `ln`

### Phase 4: Smaller command polish

- `readlink`
- `realpath`
- `basename`
- `dirname`
- `env`
- `which`
- `ping`
- `ip`
- `free`
- `uptime`

## Recommended Next Concrete Step

If we want the highest-value next move after this audit, it should be:

1. implement the universal display-selection mechanism
2. wire it into `ls`, `ps`, `df`, and `du`
3. then deepen `ls` and `ps` parity first

That gives ToSh:

- better object ergonomics
- better parity with standard Unix expectations
- one shared output-selection design instead of one-off per-command formatting hacks
