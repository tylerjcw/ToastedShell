# ToastScript Language Reference

[Back to Index](INDEX.md)

ToastScript is the scripting language of ToSh. It combines pipeline-oriented shell syntax with structured programming constructs familiar to users of C#, Lua, and modern shells.

## Comments

```tosh
# Single-line comments start with #
```

There are no multi-line comments. Use multiple `#` lines.

## Literals

### Strings

```tosh
"double-quoted string"
'single-quoted string'

# Escape sequences (double-quoted only)
"line one\nline two"
"tab\there"
"backslash: \\"

# Triple-quoted strings (preserve newlines, no escaping needed)
"""
Multi-line
string literal
"""

'''
Also multi-line
with single quotes
'''

# ANSI C-style quoting
$'escape \n sequences \t work'
$'''
triple-quoted ANSI C
'''
```

### String Interpolation

```tosh
$"Hello, {$user}!"
$"2 + 2 = {2 + 2}"
$"Files: {ls | where _.Type == file | count}"
$"Today is {(date now).DayOfWeek}"

# Triple-quoted interpolated
$"""
Name: {$name}
Count: {$items | count}
"""
```

Inside `{...}`, any ToastScript expression or pipeline is valid.

### Numbers

```tosh
42          # int
3.14        # double
0xFF        # hex int (255)
0b1010      # binary int (10)
1_000_000   # underscores for readability
```

### Booleans and Null

```tosh
true
false
null
```

### Arrays

```tosh
[1, 2, 3]
["alpha", "beta", "gamma"]
[1, "mixed", true, null]
[]                          # Empty array
```

### Record Literals

```tosh
{ Name = "Alice", Age = 30, Active = true }
```

Record literals evaluate to `ExpandoObject` (implementing `IDictionary<string, object?>`). They are anonymous — no type declaration needed.

### Ranges

```tosh
1..10          # 1 through 10 (inclusive)
1..2..10       # 1, 3, 5, 7, 9 (step of 2)
0..5           # 0, 1, 2, 3, 4, 5
```

## Variables

### Declaration

```tosh
var name = "Alice"             # Inferred type
var count = 42
string greeting = "hello"      # Explicit type — validates and converts
int answer = 42
StorageSize limit = 1gb        # Shell-native types work too
```

### Reference

```tosh
echo $name                     # Reference with $
echo $name.Length              # Member access on variable
echo $name.ToUpper()           # Method call on variable
```

### Assignment

```tosh
name = "Bob"                   # Reassign
$count += 1                    # Compound assignment (+=, -=, *=, /=, %=)
$count -= 5
```

### Member Assignment

```tosh
$record.Name = "Updated"
$obj.Property = 42
```

### Declaration Modifiers

```tosh
var localVar = 1               # Default — visible in current and child scopes
shy privateVar = 2             # Shy — only visible in declaring scope
global sharedVar = 3           # Global — visible everywhere
export publicVar = 4           # Export — exported from module scope
```

## Functions

### Basic Definition

```tosh
func greet(name) {
    echo $"Hello, {$name}!"
}
```

### Arrow Functions (Single-Expression)

```tosh
func ll => ls -la
func dirs => ls -la | where _.Type == dir
func double(x) => echo ($x * 2)
```

### Anonymous Functions

```tosh
var double = func(x) => ($x * 2)
invoke $double 21

invoke (func(x, y) => ($x + $y)) 3 4

var describe = func(x) {
    if (($x > 10)) {
        return "big"
    }

    return "small"
}
```

Anonymous `func(...)` expressions are first-class callable values. They capture lexical scope, so they can close over surrounding variables:

```tosh
var factor = 3
var scale = func(x) => ($x * $factor)
invoke $scale 7
```

For now, callables are executed explicitly with `invoke`. They are especially useful with higher-order pipeline commands such as `map`, `filter`, `reduce`, `any`, `all`, and `none`.

You can also adapt callables explicitly:

```tosh
var add = func(x, y) => ($x + $y)
var inc = partial $add 1
invoke $inc 41

var add3 = func(a, b, c) => ($a + $b + $c)
var curried = curry $add3
var step1 = invoke $curried 1
var step2 = invoke $step1 2
invoke $step2 39
```

Existing pipeline commands also accept callable values where it makes sense:

```tosh
echo 1 2 3 4 | where func(x) => ($x > 2)
echo one two | each func(x) => ($x.ToUpper())
echo 1 2 3 | select func(x) => ($x * 10)
echo pear fig banana | sort func(x) => ($x.Length)
echo ant ape bear | group-by func(x) => ($x.Substring(0, 1))
```

### Typed Parameters

```tosh
func bigFiles(minSize: StorageSize) {
    ls | where _.Size >= $minSize
}

func greet(name: String, title?: String) {    # ? marks optional
    if ($title) {
        echo $"Hello, {$title} {$name}!"
    } else {
        echo $"Hello, {$name}!"
    }
}
```

### Overloading

Top-level named functions support overloading by arity and typed parameters:

```tosh
func greet() {
    echo noargs
}

func greet(name) {
    echo hello $name
}

greet
greet toast
```

```tosh
func kind(value: int) {
    echo int
}

func kind(value: string) {
    echo string
}

kind 42
kind hello
```

Overload selection uses the same parameter-binding rules already used by ToSh class constructors and methods. If you define the same callable shape again, the new definition replaces the old overload instead of creating an ambiguous duplicate. If multiple overloads match equally well at runtime, ToSh now reports that call as ambiguous instead of silently picking one.

### Return Types

```tosh
func fileCount() -> int {
    return ls | count
}

func scriptName() -> string {
    using System.IO = IO
    return IO.Path.GetFileName($tosh.Script.Path)
}
```

### Event Handlers

```tosh
func onDirChange(event) handles DirectoryChanged {
    writeline $"Moved to {$event.NewDirectory}"
}

# With priority and when guard
func onBigCommand(event) handles CommandCompleted priority 10 when { $event.Duration > (timespan 1s) } {
    writeline $"Slow command: {$event.Command} took {$event.Duration}"
}

# One-shot handler
func onFirstStart(event) handles SessionStarted once {
    writeline "Welcome to ToSh!"
}
```

## Control Flow

### If / Else

```tosh
if ($count > 10) {
    echo "many"
} else if ($count > 0) {
    echo "some"
} else {
    echo "none"
}
```

### For Loop

```tosh
for item in (ls -la) {
    echo $item.Name
}

for i in (1..10) {
    echo $i
}

for name in (["Alice", "Bob", "Carol"]) {
    echo $"Hello, {$name}"
}
```

### While Loop

```tosh
var i = 0
while (($i < 5)) {
    echo $i
    $i += 1
}
```

### Until Loop

```tosh
var countdown = 3
until (($countdown == 0)) {
    echo $"T-{$countdown}"
    $countdown -= 1
}
```

### Switch

```tosh
switch ($color) {
    case "red" {
        echo "warm"
    }
    case "blue" {
        echo "cool"
    }
    default {
        echo "unknown"
    }
}
```

Switch also works with enum values:

```tosh
switch ($state) {
    case StockState.Low {
        echo "reorder"
    }
    case StockState.Out {
        echo "urgent"
    }
    default {
        echo "ok"
    }
}
```

### Break and Continue

```tosh
for item in ($items) {
    if ($item.Name == "skip-me") {
        continue
    }
    if ($item.Name == "stop") {
        break
    }
    echo $item.Name
}
```

### Return

```tosh
func findFirst(name: string) {
    for item in (ls) {
        if ($item.Name == $name) {
            return $item
        }
    }
    return null
}
```

## Error Handling

### Try / Catch / Finally

```tosh
try {
    var content = (cat /nonexistent/file)
    echo $content
} catch (err) {
    writeline $"Error: {$err.Message}"
} finally {
    writeline "Cleanup complete"
}
```

The `catch` variable receives the exception object. The `finally` block always runs.

### Throw

```tosh
throw "Something went wrong"
throw (new System.InvalidOperationException "Bad state")
```

## Pattern Matching

### Match Expression

```tosh
var result = match $value {
    0 => "zero"
    1 => "one"
    _ => "other"
}

# With guards
match $item {
    { _.Quantity <= 0 } => "out of stock"
    { _.Quantity < 5 } => "low stock"
    _ => "in stock"
}

# Block bodies
match $event {
    { _.Name == "critical" } => {
        writeline "ALERT!"
        raise $AlertEvent
    }
    _ => { writeline "ok" }
}
```

## Using and Require

### Using — Import CLR Namespaces

```tosh
using System.IO                # Import namespace — types available unqualified
using System.IO = IO           # Aliased import — use IO.File, IO.Path, etc.
```

`using` is lexically scoped — it only applies within the block or function where it appears.

### Require — Load ToSh Scripts and Modules

```tosh
require "./mylib.tosh"                           # Execute script
require Inventory from "./toastlib.tosh"         # Import specific module
require Inventory from "./toastlib.tosh" as Inv  # With alias
require { Reporting, Utilities } from "./toastlib.tosh"  # Multiple modules

# Native library binding
require native "./libcrypto.so" as Crypto
```

## Nameof

```tosh
var x = 42
echo (nameof(x))     # "x"
echo (nameof($x))    # "x"
```

## Special Variables

| Variable | Description |
|----------|-------------|
| `$tosh` | Runtime namespace — access to config, last result, script info |
| `$env.NAME` | Direct environment-variable value lookup, such as `$env.PATH` |
| `$tosh.Last.Result` | Most recent successful statement result |
| `$tosh.Config` | Live configuration object |
| `$tosh.Script.Path` | Current script file path (when running a script) |
| `_` | Current pipeline item in predicates (`where`, `each`, `sort`, etc.) |
| `$this` | Self-reference inside class methods |

## Reserved Keywords

`var`, `func`, `class`, `record`, `enum`, `event`, `module`, `if`, `else`, `for`, `in`, `while`, `until`, `switch`, `case`, `default`, `break`, `continue`, `return`, `throw`, `try`, `catch`, `finally`, `using`, `require`, `bind`, `alloc`, `true`, `false`, `null`

## See Also

- [Operators and Expressions](OPERATORS.md)
- [Type System](TYPES.md)
- [Modules and Types](MODULES.md)
- [Pipeline Model](PIPELINES.md)
