# Modules and User-Defined Types

[Back to Index](INDEX.md)

ToSh supports user-defined modules, classes, records, enums, and events — providing structured programming capabilities within the shell scripting language.

## Modules

Modules group related functions, types, and state into a namespace:

```tosh
module Utilities {
    func banner(title: string) {
        writeline ""
        styled $"=== {$title} ===" --fg bright-cyan --bold | writeline
    }

    func script_name() -> string {
        using System.IO = IO
        return IO.Path.GetFileName($tosh.Script.Path)
    }
}
```

### Using Module Members

Access module members with dot notation:

```tosh
Utilities.banner("Hello")
echo (Utilities.script_name())
```

### Nested Types

Modules can contain classes, records, enums, and other modules:

```tosh
module Inventory {
    enum StockState { Healthy, Low, Out }
    record Item(Name: string, Quantity: int)
    class Shelf(label: string) { ... }

    func sample_items() {
        return [new Item("Bread", 2), new Item("Coffee", 1)]
    }
}
```

## Classes

Classes define objects with properties, methods, and a primary constructor:

```tosh
class Shelf(label: string) {
    prop Label: string = label
    prop Items = new list()
    prop Accent: System.Drawing.Color => (System.Drawing.Color.Green)
    prop ItemCount: int => ($this.Items | flatten | count)

    func Add(item) -> Shelf {
        $this.Items.Add($item) | ignore
        return $this
    }

    func Remove(name: string) -> bool {
        var index = -1
        var i = 0
        for item in ($this.Items) {
            if ($item.Name == $name) {
                $index = $i
                break
            }
            $i += 1
        }
        if ($index >= 0) {
            $this.Items.RemoveAt($index) | ignore
            return true
        }
        return false
    }
}
```

### Primary Constructor

The parenthesized parameters after the class name form the primary constructor. These parameters are available in property initializers:

```tosh
class Point(x: double, y: double) {
    prop X: double = x
    prop Y: double = y
    prop Magnitude: double => (Math.Sqrt(($this.X * $this.X) + ($this.Y * $this.Y)))
}

var p = new Point(3.0, 4.0)
echo $p.X                  # 3
echo $p.Magnitude           # 5
```

### Properties

| Syntax | Description |
|--------|-------------|
| `prop Name: Type = value` | Stored property with initial value |
| `prop Name = value` | Stored property (inferred type) |
| `prop Name: Type => (expr)` | Computed property (evaluated on each access) |

### The `$this` Reference

Inside class methods, `$this` refers to the current instance:

```tosh
class Counter() {
    prop Count: int = 0

    func Increment() -> Counter {
        $this.Count = ($this.Count + 1)
        return $this
    }
}
```

### Constructing Class Instances

```tosh
var shelf = new Shelf("Pantry")
var shelf = new Inventory.Shelf("Pantry")   # From module
```

## Records

Records define immutable value types with named fields:

```tosh
record Item(
    Name: string,
    Quantity: int,
    Category?: string = "General",
    Tint?: string = "White")
```

### Record Features

- **Named fields** with types and optional defaults
- **Optional fields** marked with `?` — default to `null` if not provided
- **Default values** — evaluated when the record is constructed without a value for that field
- **Value equality** — two records with the same field values are equal

### Constructing Records

```tosh
var item = new Item("Bread", 2, "Food", "BurlyWood")
var item = new Item("Bread", 2)             # Uses defaults for Category and Tint

echo $item.Name                              # "Bread"
echo $item.Category                          # "General" (default)
```

### Record Equality

```tosh
var a = new Item("Bread", 2)
var b = new Item("Bread", 2)
echo ($a == $b)                              # true
```

## Enums

Enums define named constant sets:

```tosh
enum StockState {
    Unknown
    Healthy
    Low
    Out
}
```

### Using Enum Values

```tosh
var state = StockState.Healthy
echo $state                                  # "Healthy"
echo ($state == StockState.Healthy)          # true
```

### Enums in Switch

```tosh
switch ($state) {
    case StockState.Low {
        echo "reorder needed"
    }
    case StockState.Out {
        echo "urgent restock!"
    }
    default {
        echo "stock is fine"
    }
}
```

### Enum Base Types

```tosh
enum Permissions : int {
    None = 0
    Read = 4
    Write = 2
    Execute = 1
}
```

## Events (User-Defined)

Events define named notification types with fields:

```tosh
event BuildCompleted {
    Project = ""
    Duration = (timespan 0s)
    Success = true
}
```

See the [Event System](EVENTS.md) reference for full details on events, handlers, and the event bus.

## The `require` System

### Loading Scripts

```tosh
require "./utils.tosh"                       # Execute script in current scope
```

### Importing Modules

```tosh
require Inventory from "./toastlib.tosh"
require Inventory from "./toastlib.tosh" as Inv  # With alias
require { Reporting, Utilities } from "./toastlib.tosh"  # Multiple modules
```

When importing modules, only the specified modules are imported into the current scope. The script executes in an isolated scope and only the named exports are visible.

### Module Aliasing

```tosh
require Inventory from "./toastlib.tosh" as Inv

var items = Inv.sample_items()
var shelf = new Inv.Shelf("Pantry")
var state = Inv.StockState.Healthy
```

### Native Library Loading

```tosh
require native "./libcrypto.so" as Crypto

bind Crypto {
    func sha256(data: byte-ptr, len: uint64) -> byte-ptr
}
```

### Script Shebangs

ToSh scripts can use a shebang line:

```tosh
#!/usr/bin/env tosh

echo "This is a ToSh script"
ls | first 3
```

Make the script executable with `chmod +x script.tosh` and run it directly.

## Declaration Modifiers

All type and function declarations support modifiers:

| Modifier | Effect |
|----------|--------|
| (none) | Default scope — visible in current and child scopes |
| `shy` | Private to declaring scope only |
| `global` | Visible in all scopes |
| `export` | Exported from module scope to parent |

```tosh
module MyLib {
    # Exported (visible to importers)
    export func publicApi() { ... }

    # Not exported (internal to module)
    shy func internalHelper() { ... }

    # Global (available everywhere)
    global func utilityFunction() { ... }
}
```

## Practical Examples

### Library Pattern

`toastlib.tosh`:
```tosh
module Utilities {
    func banner(title: string) {
        writeline ""
        styled $"=== {$title} ===" --fg bright-cyan --bold | writeline
    }
}

module Inventory {
    enum StockState { Healthy, Low, Out }
    record Item(Name: string, Quantity: int, Category?: string = "General")

    func state_of(item: Item) -> StockState {
        if ($item.Quantity <= 0) { return StockState.Out }
        if ($item.Quantity < 3) { return StockState.Low }
        return StockState.Healthy
    }

    func restock(item: Item, amount: int) -> Item {
        return new Item($item.Name, ($item.Quantity + $amount), $item.Category)
    }
}
```

`main.tosh`:
```tosh
require { Utilities, Inventory } from "./toastlib.tosh"

Utilities.banner("Inventory Check")

var items = [
    new Inventory.Item("Bread", 2, "Food"),
    new Inventory.Item("Coffee", 0, "Food"),
    new Inventory.Item("Soap", 6, "Household")
]

$items | each {
    var state = Inventory.state_of(_)
    writeline $"[{$state}] {_.Name} x{_.Quantity}"
}
```

### Class with Fluent Interface

```tosh
class QueryBuilder() {
    prop Filters = new list()
    prop SortField: string = ""
    prop Limit: int = 0

    func Where(predicate: string) -> QueryBuilder {
        $this.Filters.Add($predicate) | ignore
        return $this
    }

    func OrderBy(field: string) -> QueryBuilder {
        $this.SortField = $field
        return $this
    }

    func Take(n: int) -> QueryBuilder {
        $this.Limit = $n
        return $this
    }

    func Describe() -> string {
        return $"Filters: {$this.Filters | flatten | join-lines ', '}, Sort: {$this.SortField}, Limit: {$this.Limit}"
    }
}

var query = new QueryBuilder() | call Where "active" | call OrderBy "name" | call Take 10
echo (query.Describe())
```

## See Also

- [Language Reference](LANGUAGE.md) — Syntax for all type definitions
- [Event System](EVENTS.md) — User-defined events
- [CLR Interoperability](CLR_INTEROP.md) — Mixing CLR and ToSh types
