# CLR Interoperability

[Back to Index](INDEX.md)

ToSh runs on .NET and provides seamless access to the full CLR runtime. You can construct any .NET type, call any method, access any property, and load any assembly — all from the shell.

## Constructing Objects

### Using `new`

```tosh
new System.Guid "d3b07384-d113-4ec6-a5d2-5b1d0d2e8c39"
new System.Random
new System.Text.StringBuilder "initial"
new System.IO.FileInfo "/etc/passwd"
new System.Net.Http.HttpClient
new System.Collections.Generic.List[int]
```

### With `using` Aliases

```tosh
using System.IO = IO
var info = new IO.FileInfo "/etc/passwd"
echo $info.Length
```

## Calling Methods

### Instance Methods

Call methods directly on objects using dot notation:

```tosh
"hello, world".ToUpper()                    # "HELLO, WORLD"
"hello, world".Substring(0, 5)              # "hello"
"hello, world".Replace("world", "tosh")     # "hello, tosh"
"hello, world".Split(",")                   # ["hello", " world"]
"hello".Contains("ell")                     # true
```

### Static Methods

Reference static methods using `Type.Method(args)` syntax:

```tosh
String.Join " " ["hello", "world"]          # "hello world"
String.IsNullOrEmpty ""                     # true
Math.Round Math.PI 4                        # 3.1416
Math.Max 10 20                              # 20
Int32.Parse "42"                            # 42
Guid.NewGuid                                # Random GUID
```

### Static Properties

```tosh
Math.PI                                     # 3.141592653589793
Int32.MaxValue                              # 2147483647
DateTime.Now                                # Current datetime
Environment.MachineName                     # Hostname
Environment.OSVersion                       # OS info
```

### The `call` Command

For dynamic method invocation:

```tosh
var rng = new System.Random
call $rng Next 1 100                        # Call instance method
call String Join ", " ["a", "b", "c"]       # Call static method
```

### Method Chaining

```tosh
"  hello  ".Trim().ToUpper()                # "HELLO"
new System.Text.StringBuilder "a" | call Append "b" | call Append "c" | call ToString
```

## Accessing Properties

### Instance Properties

```tosh
var info = new System.IO.FileInfo "/etc/passwd"
echo $info.Length                            # File size in bytes
echo $info.Exists                           # true/false
echo $info.Extension                        # ".passwd" or ""
echo $info.Directory.FullName               # "/etc"
```

### Nested Access

```tosh
(date now).DayOfWeek                        # "Monday" etc.
(date now).Year                             # 2026
$tosh.Config.Prompt.NameText                # Current prompt name
```

## Loading Assemblies

```tosh
load-assembly /path/to/MyLibrary.dll
new MyNamespace.MyType
```

Loaded assemblies are available for the remainder of the session. Types from loaded assemblies can be used with `new`, static method calls, `using`, and type parameters.

## Type Discovery

### Finding Types

```tosh
types                                       # List all available types
types string                                # Search for types matching "string"
types -a System.IO                          # All types in System.IO
```

### Inspecting Types

```tosh
describe-type System.IO.File                # Full type description
members System.String                       # All members
methods System.String                       # Methods only
constructors System.Guid                    # Constructors only
```

### Inspecting Objects

```tosh
ls | first | type-of                        # Object's type name
ls | first | members                        # All members on instance
ls | first | inspect                        # Shape and preview
ls | first | get-props                      # Property names
ls | first | get-methods                    # Method names
```

## The `using` Statement

Import CLR namespaces to use unqualified type names:

```tosh
using System.IO
var info = new FileInfo "/etc/passwd"       # No System.IO. prefix needed

using System.Text.RegularExpressions
var re = new Regex "\\d+"
```

### Aliased Imports

```tosh
using System.IO = IO
IO.File.ReadAllText("/etc/hostname")
IO.Path.Combine("/tmp", "myfile.txt")
```

### Scoping

`using` is lexically scoped — it only applies within the block or function where it appears:

```tosh
func readConfig() {
    using System.IO = IO
    return IO.File.ReadAllText("config.json")
}
# IO is not available here
```

## Type Casting

```tosh
cast int "42"                               # String → int
cast double "3.14"                          # String → double
cast string 42                              # int → string
cast System.DateTime "2024-03-15"           # String → DateTime
```

### Generic Type Construction

```tosh
cast List[int] [1, 2, 3]                   # object[] → List<int>
cast Dictionary[string,int] $dict           # Cast to typed dictionary
new System.Collections.Generic.List[string]  # Construct generic type
```

## Common .NET Patterns

### File Operations

```tosh
using System.IO = IO
IO.File.ReadAllText("/etc/hostname")
IO.File.WriteAllText("/tmp/test.txt", "hello")
IO.File.Exists("/etc/passwd")
IO.Directory.GetFiles("/tmp", "*.txt")
IO.Path.GetFileName("/path/to/file.txt")    # "file.txt"
IO.Path.GetExtension("/path/to/file.txt")   # ".txt"
IO.Path.Combine("/tmp", "sub", "file.txt")  # "/tmp/sub/file.txt"
```

### String Operations

```tosh
String.Join ", " ["a", "b", "c"]
String.Format "Hello, {0}!" "World"
String.IsNullOrWhiteSpace "  "              # true
"hello".PadLeft(10, '*')                    # "*****hello"
"hello world".Split(' ')                    # ["hello", "world"]
```

### Math Operations

```tosh
Math.Abs -42                                # 42
Math.Ceiling 3.2                            # 4
Math.Floor 3.8                              # 3
Math.Pow 2 10                               # 1024
Math.Sqrt 144                               # 12
Math.Log 100 10                             # 2
Math.Min 5 3                                # 3
Math.Max 5 3                                # 5
```

### Collections

```tosh
using System.Linq

var numbers = [1, 2, 3, 4, 5]
echo ($numbers | flatten | where _ > 2)     # ToSh-style
# Or .NET-style:
Enumerable.Range(1, 10)
```

### HTTP Requests

```tosh
using System.Net.Http

var client = new HttpClient
var response = ($client.GetStringAsync("https://api.example.com/data") | call Result)
echo $response | from-json
```

### Regular Expressions

```tosh
using System.Text.RegularExpressions

var match = Regex.Match("abc123" "\\d+")
echo $match.Value                           # "123"
echo $match.Success                         # true
```

## Interop with External Processes

### From External to Typed

```tosh
# Parse external command output into typed objects
git log --format='{"hash":"%H","author":"%an","message":"%s"}' -5 | each { from-json }

# Use ToSh's data commands on external output
curl -s "https://api.example.com/data" | from-json | flatten | where _.active == true
```

### From Typed to External

```tosh
# Materialize objects to temporary files for external tools
ls | to-json | as-file json | each { /bin/jq '.[] | .Name' $_.FullName }

# Process substitution
/bin/diff <(cat file1.txt) <(cat file2.txt)
```

## See Also

- [Type System](TYPES.md) — Type aliases and automatic conversion
- [Commands Reference](COMMANDS.md) — `new`, `cast`, `call`, `load-assembly`
- [Modules and Types](MODULES.md) — User-defined types
