# Pipeline Model

[Back to Index](INDEX.md)

The pipe operator `|` connects commands and expressions into an object pipeline.

```tosh
ls -la | where _.Type == file | sort Size | reverse | first 5
```

Each stage receives pipeline input as streamed CLR values and can emit more CLR values to the next stage.

## Stages

A pipeline stage is either:

- a command invocation
- an expression

Examples:

```tosh
ls -la | where _.Size > 1mb
call System.String Join ", " ["a", "b"]
echo (2 + 2)
```

## Collections And Row Semantics

Collections are still values in ToSh. The pipeline itself does not globally auto-flatten them.

```tosh
echo [1, 2, 3]
echo [1, 2, 3] | collect
echo [1, 2, 3] | flatten
```

Those mean:

- `echo [1, 2, 3]`: emit one array object
- `| collect`: keep the current row boundaries and gather them into one list
- `| flatten`: explicitly expand one level of nested enumeration

For convenience, some row-oriented commands replay a **single** collection input as rows:

```tosh
echo [1, 2, 3] | first      # 1
echo [1, 2, 3] | get 0      # 1
echo [1, 2, 3] | count      # 3
echo [1, 2, 3] | where { _ > 1 }
echo [3, 1, 2] | sort
```

If you need exact control over row shape, prefer `flatten` and `collect` explicitly.

## Current Item

In predicate and block contexts, `_` is the current pipeline item:

```tosh
ls | where _.Size > 1mb
ls | each { echo _.Name }
ls | sort Size
```

## Capture

Use:

- `(...)` to capture exactly one object value
- `$(...)` to capture text

```tosh
var firstFile = (ls | first)
var user = $(whoami)
```

## Redirection

ToSh keeps `<` and `>` as expression operators. Shell redirection uses explicit forms:

```tosh
echo hello out> ./hello.txt
/bin/sh -c "printf out\\n; printf err\\n >&2" err> ./stderr.txt
/bin/sh -c "printf out\\n; printf err\\n >&2" o+e> ./combined.txt
```

Supported forms include:

- `out>` / `out>>`
- `err>` / `err>>`
- `o+e>` / `o+e>>`
- `e+o>` / `e+o>>`
- `<<<` for here-strings

## External Commands

Unknown command names fall through to native process execution:

```tosh
git status
/bin/ls -la
python3 script.py
```

External commands:

- receive piped text on stdin when appropriate
- emit stdout as text lines
- preserve exit status through `$tosh.Last.ExitCode`

## Background Jobs

Append `&` to run a native pipeline in the background:

```tosh
/bin/sleep 10 &
jobs
wait-for 1
```

## Final Rendering

The display system chooses a view based on the output:

- one object: record-style or scalar view
- homogeneous row set: table view
- mixed values: value-by-value rendering

Use `view` and `$tosh.Config.Display` to change how values render without changing what the pipeline carries.
