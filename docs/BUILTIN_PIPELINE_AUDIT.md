# Builtin Pipeline Audit

This document records the intended pipeline-input contract for every builtin command currently registered in ToSh.

## Legend

- `object`: consumes arbitrary piped `.NET` objects as values
- `scalar`: consumes piped strings, numbers, booleans, enums, or `ShellTextLine` values meaningfully
- `path-like`: consumes piped strings, `FileSystemEntry`, or `FileSystemInfo` values as filesystem paths
- `collection`: consumes a collection object as a single value

Important rules:

- ToSh does **not** globally auto-flatten collection objects in pipelines.
- `flatten` is still the explicit “expand one level” tool.
- Some row-oriented commands intentionally replay a **single** collection input as row values for convenience.
- If a command says it accepts `collection` input, that means it can also work with a collection object as a single piped value.

Precedence rule:

- If a builtin supports both explicit arguments and pipeline input, explicit arguments win unless noted otherwise.

## Newly Normalized In This Pass

These commands now consume piped input in a deliberate, documented way:

- `help`
- `apropos`
- `types`
- `which`, `whence`
- `env`
- `history-search`
- `df`, `mounts`
- `du`, `usage`, `disk-usage`
- `find`
- `first`
- `get`
- `where`
- `sort`
- `count`

### Single-Collection Replay

The pipeline itself keeps collection values intact. The following commands are special-cased to replay a lone collection input as row values:

- `first`
- `get` when indexing (`get 0`, `get 1..3`)
- `where`
- `sort`
- `count`

That means these behave element-wise:

```tosh
echo [1, 2, 3] | first
echo [1, 2, 3] | get 0
echo [1, 2, 3] | where { _ > 1 }
echo [3, 1, 2] | sort
echo [1, 2, 3] | count
```

For exact control over row shape, keep using `flatten` and `collect` explicitly.

## Discovery, Help, And Session Commands

| Commands | Object | Scalar | Path-like | Collection | Notes |
| --- | --- | --- | --- | --- | --- |
| `help` | no | yes | no | no | With no args, piped values are treated as help topics. `search` and `related` also use piped query/topic text when their positional input is omitted. |
| `apropos` | no | yes | no | no | Uses piped scalar text as the fuzzy search query when no query arg is supplied. |
| `types` | no | yes | no | no | Uses piped scalar text as a type filter when no filter arg is supplied. |
| `which`, `whence` | no | yes | no | no | Uses piped scalar text as command names when no explicit names are supplied. |
| `env` | no | yes | no | no | With no args and no pipeline input, lists all environment variables. With piped scalar input, treats each item as a variable name lookup. |
| `history-search` | no | yes | no | no | Uses piped scalar text as the search term when no explicit search text is supplied. |
| `history` | no | no | no | no | Producer only; ignores pipeline input. |
| `view` | no | no | no | no | Session/config command; ignores pipeline input. |
| `clear`, `exit` | no | no | no | no | Session-control commands; ignore pipeline input. |

## Filesystem, System, And Process Inspection

| Commands | Object | Scalar | Path-like | Collection | Notes |
| --- | --- | --- | --- | --- | --- |
| `pwd`, `uname`, `hostname`, `whoami`, `id`, `free`, `uptime`, `ping`, `ps`, `jobs`, `seq`, `sleep` | no | no | no | no | Producer-only commands; ignore pipeline input. |
| `cd` | no | no | yes | no | Accepts piped path-like input when no explicit path arg is supplied. Falls back to the user home directory when neither args nor pipeline input are present. |
| `ls` | no | no | no | no | Still arg-first. Path input must be explicit for now. |
| `df`, `mounts` | no | no | yes | no | With no args and no pipeline input, lists all mounted filesystems. With args or piped path-like input, returns the matching mount(s). |
| `du`, `usage`, `disk-usage` | no | no | yes | no | Uses piped path-like input as roots when no explicit roots are supplied. Falls back to the current directory when neither are present. |
| `stat` | no | no | yes | no | Uses path-like args or piped path-like input. |
| `find` | no | no | yes | no | Uses piped path-like roots when no explicit roots are supplied. Falls back to the current directory when neither are present. |
| `readlink`, `realpath` | no | no | yes | no | Use `ShellPathArguments` path-like resolution. |
| `dirname`, `basename` | no | yes | yes | no | Accept path-like pipeline input, but treat it as scalar path text, not as file contents. |
| `wait-for`, `kill`, `signal` | yes | yes | no | no | Accept job/process objects from the pipeline, plus scalar job ids or pids. |

## Output, Prompt, And Text Rendering

| Commands | Object | Scalar | Path-like | Collection | Notes |
| --- | --- | --- | --- | --- | --- |
| `echo` | no | no | no | no | Producer only; emits its explicit arguments as pipeline objects. |
| `write`, `writeline` | yes | yes | yes | yes | Render the entire pipeline or explicit args to the terminal. Collections are rendered as single values/batches, not auto-expanded. |
| `styled` | no | no | no | no | Producer only; builds styled text from explicit args. |
| `prompt-text`, `prompt-time`, `prompt-dir`, `prompt-git`, `prompt-userhost`, `prompt-jobs`, `prompt-duration`, `prompt-exit`, `prompt-newline` | no | no | no | no | Prompt-segment producers; ignore pipeline input. |
| `readline` | no | no | no | no | Interactive input producer; ignores pipeline input. |

## Text, Parsing, Structured Data, And Materialization

| Commands | Object | Scalar | Path-like | Collection | Notes |
| --- | --- | --- | --- | --- | --- |
| `cat` | no | no | yes | no | Reads piped path-like values as file paths. |
| `head`, `tail` | yes | yes | no | yes | With file args, operate on file text. With pipeline input, operate on pipeline items directly. They do not reinterpret piped path-like values as file contents. |
| `wc` | yes | yes | yes | yes | With file args, counts file contents. With pipeline input, counts rendered pipeline text. A collection object counts as one piped value unless flattened first. |
| `uniq` | yes | yes | yes | yes | Preserves original objects unless `-c` is used. |
| `cut`, `tr`, `grep`, `split`, `join-lines`, `replace`, `parse`, regex `match` | no | yes | no | no | Text/scalar pipeline consumers. |
| `from-json`, `from-csv`, `from-tsv`, `from-xml` | no | yes | no | no | Parse text/scalar pipeline input into typed objects. |
| `template`, `hash` | yes | yes | yes | yes | Consume general pipeline values by serializing or templating them. |
| `to-json`, `to-csv`, `as-file` | yes | yes | yes | yes | Materialize pipeline values. Collections are preserved as single values unless previously flattened. |

## Filesystem Mutation And Path Predicates

| Commands | Object | Scalar | Path-like | Collection | Notes |
| --- | --- | --- | --- | --- | --- |
| `mkdir`, `touch`, `rm`, `chmod`, `chown` | no | no | yes | no | Use piped path-like input when explicit args are omitted. |
| `cp`, `mv`, `ln` | no | no | no | no | Still explicit-arg only because they require multi-path source/destination forms. |
| `exists`, `is-file`, `is-dir`, `is-link` | no | no | yes | no | Path predicate commands accept piped path-like input. |
| `mkdir-temp`, `tempfile` | no | no | no | no | Producer-only helpers. |

## Pipeline Shaping, Aggregation, And Control

| Commands | Object | Scalar | Path-like | Collection | Notes |
| --- | --- | --- | --- | --- | --- |
| `get`, `select`, `pick` | yes | yes | yes | replay/index | Member projection keeps collection values intact, but index/range access replays a single collection input as rows before indexing. |
| `rename`, `inspect`, `last`, `skip`, `reverse`, `distinct`, `group-by`, `take-while`, `skip-while`, `tee` | yes | yes | yes | single | These commands treat a collection object as one pipeline value unless you `flatten` first. |
| `where`, `first`, `sort`, `sort-by` | yes | yes | yes | replay | These commands replay a single collection input as row values for convenience, but preserve normal row boundaries when the pipeline already has multiple items. |
| `each` | yes | yes | yes | expand-per-item | `each` expands enumerable members of each incoming item before running the block. |
| `count` | yes | yes | yes | replay | Counts row values, and replays a single collection input as rows before counting. |
| `flatten` | yes | yes | yes | yes | Explicitly expands enumerable pipeline values into their items. |
| `sum`, `avg`, `average`, `min`, `max` | yes | yes | no | single | Aggregate over piped items. A collection object is a single value unless flattened first. |
| `summarize`, `summary` | yes | yes | yes | single | Consume the current pipeline rows and return only `ColumnSummary` objects for the requested aggregate operations. A collection object is treated as one input row unless flattened first. |
| `date` | yes | no | no | no | Pipeline form expects date/time objects and applies `add` / `sub`. |
| `timespan` | no | no | no | no | Producer/parser command from explicit args only. |
| `ignore` | yes | yes | yes | yes | Consumes and discards any pipeline input. |
| `xargs` | yes | yes | yes | single | Treats pipeline items as values to append into a nested command invocation. |

## Reflection, Types, And Object Manipulation

| Commands | Object | Scalar | Path-like | Collection | Notes |
| --- | --- | --- | --- | --- | --- |
| `type-of`, `describe-type`, `members`, `methods`, `constructors`, `cast`, `call`, `call-method`, `clone`, `has-prop`, `has-method`, `get-props`, `get-methods`, `get-prop`, `set-prop`, `del-prop` | yes | yes | yes | single | Reflection/object commands operate on piped objects directly. Collection objects remain single inputs unless flattened first. |
| `load-assembly` | no | no | yes | no | Accepts path-like args or piped path-like input. |
| `new` | no | no | no | no | Constructor command; explicit arguments only. |

## Shell State And Miscellaneous

| Commands | Object | Scalar | Path-like | Collection | Notes |
| --- | --- | --- | --- | --- | --- |
| `export`, `forget`, `unset` | no | no | no | no | Session/module state commands remain explicit-arg only. |

## Follow-Up Rules

When adding or changing a builtin:

1. Decide explicitly whether it should accept object, scalar, path-like, and collection pipeline input.
2. Prefer `ShellPathArguments` for path-like input.
3. Prefer `TextInputUtilities` for scalar/text input.
4. Do not auto-flatten collections unless the command is explicitly about expansion.
5. Add tests for both the explicit-argument form and the pipeline-input form if both are supported.
