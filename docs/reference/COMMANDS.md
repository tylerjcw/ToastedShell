# Command Map

[Back to Index](INDEX.md)

ToSh has a large built-in command surface. This page is the curated command map: it shows the major command families and the commands you are most likely to reach for first.

For the full live surface of any command, including flags, aliases, and examples, use:

- `help <command>`
- `help --cli`
- `help browse`

In the interactive REPL:

- `F1` opens `help --cli` seeded from the token under the cursor
- `Alt+H` opens the same inline help browser on terminals that do not expose function keys cleanly
- `F2` tries to inline-inspect the reference under the cursor
- `Alt+I` opens the same inline inspector on terminals that do not expose function keys cleanly
- `i` inside inline help or inspect inserts back into the active line at the cursor

## Discovery And Session

| Commands | Purpose |
|----------|---------|
| `help`, `apropos` | Help lookup, search, the inline fuzzy help browser, and the full-screen help browser |
| `history`, `history-search` | Interactive and persisted history management |
| `config` | Live config access, reload, init, and the full-screen config editor |
| `view` | Display mode and per-type rendering preferences |
| `exit`, `exec`, `clear` | Session control |

Useful examples:

```tosh
help --cli
help browse
help summarize
apropos prompt
config browse
view timespan scalar long
```

## Navigation And Filesystem Work

| Commands | Purpose |
|----------|---------|
| `pwd`, `cd`, `back`, `forward`, `dirs` | Directory navigation and directory stack |
| `ls`, `tree`, `find`, `glob`, `stat`, `read-link`, `realpath`, `dirname`, `basename` | Filesystem listing and inspection |
| `mkdir`, `mkdir-temp`, `touch`, `rm`, `cp`, `mv`, `ln`, `chmod`, `chown`, `tempfile` | Filesystem mutation |
| `exists`, `is-file`, `is-dir`, `is-link` | Path predicates |

Useful examples:

```tosh
ls -la | where _.Type == file | sort Size | reverse | first 10
ls --show Name,FullName,Size
tree -adfL 2
find . -name "*.tosh"
```

## File And Stream I/O

| Commands | Purpose |
|----------|---------|
| `cat`, `read-file`, `read-lines`, `read-bytes` | Read file content |
| `write-file`, `append-file`, `write-bytes` | Write file content |
| `open-file`, `close`, `read-from`, `read-line-from`, `read-to-end`, `write-to`, `write-line-to`, `flush`, `seek`, `position`, `length`, `copy-to` | Explicit managed handle workflows |

Useful examples:

```tosh
read-file ./config.json | from-json
write-file ./notes.txt "hello"
var reader = (open-file ./notes.txt)
read-line-from $reader
close $reader
```

## Shaping And Summarizing Pipelines

| Commands | Purpose |
|----------|---------|
| `where`, `each`, `foreach`, `get`, `select`, `rename`, `inspect`, `invoke`, `partial`, `curry`, `map`, `filter`, `reduce`, `any`, `all`, `none` | Filter, transform, project, adapt, call, and fold/query streams with first-class shell callables |
| `first`, `last`, `skip`, `take-while`, `skip-while` | Window and slice the current stream |
| `flatten`, `collect`, `tee` | Reshape or branch pipeline output |
| `sort`, `reverse`, `distinct`, `uniq`, `group-by` | Order and group data |
| `count`, `sum`, `average`, `avg`, `min`, `max`, `summarize`, `summary` | Aggregate values and rows |

Useful examples:

```tosh
ls | where _.Extension == .cs | get { Name, Size }
echo [1, 2, 3] | first
lsblk -l | summarize --sum Size
df | summarize _.Used
invoke (func(x) => ($x * 2)) 21
echo 1 2 3 | map func(x) => ($x * 2)
echo 1 2 3 | select func(x) => ($x * 10)
echo one two | foreach func(x) => ($x.ToUpper())
func greet() { echo noargs }; func greet(name) { echo hello $name }; greet; greet toast
var add = func(x, y) => ($x + $y); var inc = partial $add 1; invoke $inc 41
echo 1 2 3 4 | reduce 0 func(acc, x) => ($acc + $x)
```

## Text, Regex, And Serialization

| Commands | Purpose |
|----------|---------|
| `wc`, `split`, `join-lines`, `cut`, `tr`, `xargs` | Text shaping |
| `grep`, `match`, `parse`, `replace` | Regex and pattern work |
| `from-json`, `from-csv`, `from-tsv`, `from-xml` | Parse structured text into objects |
| `to-json`, `to-csv`, `template`, `hash`, `as-file` | Serialize and materialize data |

Useful examples:

```tosh
cat ./data.csv | from-csv | where _.Score > 90
echo "2026-04-03" | parse "(?<year>\\d{4})-(?<month>\\d{2})-(?<day>\\d{2})"
ls | first 5 | to-json
```

## Time, Identity, And Utility Values

| Commands | Purpose |
|----------|---------|
| `seq`, `sleep` | Sequences and delays |
| `date` | Current time, parsing, formatting, and `DateOnly` / `TimeOnly` projection |
| `timespan` | Construct `TimeSpan` values |
| `guid` | Create, parse, inspect, and format GUIDs |

Useful examples:

```tosh
date now
date -dt now
date parse 2026-04-03T12:34:56Z -d
guid new
guid new v7
guid info 0195e7d1-4b88-7f7a-9a34-12ab34cd56ef
```

## Processes, Jobs, And System State

| Commands | Purpose |
|----------|---------|
| `ps`, `jobs`, `wait-for`, `kill`, `signal` | Process and job management |
| `env`, `vars`, `which`, `whence` | Environment and command discovery |
| `uname`, `hostname`, `whoami`, `id`, `uptime`, `free`, `ping` | Common system information |
| `df`, `du`, `findmnt`, `lsblk`, `lscpu`, `lsfd`, `lsipc`, `ip` | Typed Unix / Linux data adapters |

Useful examples:

```tosh
ps -f --sort -Id
env -- PATH=/tmp/bin git status
ip addr | where { _.State == up }
findmnt -l | where _.FsType == ext4
```

## CLR And Object Introspection

| Commands | Purpose |
|----------|---------|
| `type-of`, `describe-type`, `members`, `methods`, `constructors`, `types` | Inspect types and members |
| `new`, `cast`, `call`, `call-method`, `load-assembly` | CLR interop |
| `get-prop`, `set-prop`, `del-prop`, `has-prop`, `has-method`, `get-props`, `get-methods`, `clone`, `ignore`, `export`, `forget`, `unset` | Object/property utilities |

Useful examples:

```tosh
new System.Net.IPEndPoint 127.0.0.1 8080
call System.String Join ", " ["a", "b", "c"]
ls | first | members
describe-type System.Net.NetworkInformation.NetworkInterface
```

## Native Interop

| Commands | Purpose |
|----------|---------|
| `alloc`, `native-alloc` | Allocate unmanaged buffers |
| `native-free`, `read-buffer`, `write-buffer`, `size-of`, `offset-of` | Work with native memory and layouts |

Native library binding is done through language features:

```tosh
require native libc.so.6 as LibC
bind LibC { func abs(int) -> int }
```

## Prompt And TUI Commands

| Commands | Purpose |
|----------|---------|
| `prompt-time`, `prompt-dir`, `prompt-git`, `prompt-userhost`, `prompt-history`, `prompt-jobs`, `prompt-duration`, `prompt-exit`, `prompt-text`, `prompt-newline` | Prompt segment builders |
| `tui pick`, `tui filter`, `tui input`, `tui confirm`, `tui filepick`, `tui run` | Reusable TUI entry points |

Useful examples:

```tosh
prompt-history
tui pick --cli [one, two, three]
tui input "Project name"
```

## Notes

- ToSh built-ins return objects unless a command is explicitly text-oriented.
- Display options such as `--show`, `--hide`, and `view columns` change rendering, not the underlying pipeline objects.
- Use `help <command>` for the authoritative current flag and alias surface of a specific command.
