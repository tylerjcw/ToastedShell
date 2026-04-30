// Tosh.ParityCheck — build-time advisory tool.
//
// Runs the same parity & documentation completeness checks as the unit tests,
// but emits warnings in MSBuild canonical diagnostic format on stdout. Wired
// into the build via Directory.Build.targets with `IgnoreExitCode="true"` and
// `ConsoleToMSBuild="true"`, so problems surface as warnings without ever
// failing the build.
//
// Output format:  origin(line,col): warning TOSH###: message
//   TOSH001 — registered command lacks an explicit [CommandCategory]
//   TOSH002 — registered command has no [CommandExample] entries
//   TOSH003 — registered command has no [CommandOutput] description
//   TOSH004 — parser hardcodes a command name that does not resolve via the registry
//   TOSH005 — operator parity: lexer/parser/evaluator drift suspected

using System.Reflection;
using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language;

var repoRoot = LocateRepoRoot();
if (repoRoot is null)
{
    Console.Error.WriteLine("Tosh.ParityCheck: could not locate repo root from cwd; skipping.");
    return 0;
}

var warnings = new List<string>();

try { CheckDocumentationCompleteness(repoRoot, warnings); }
catch (Exception ex) { warnings.Add(FormatWarning(repoRoot, "src/Tosh.Runtime/Tosh.Runtime.csproj", "TOSH900", $"doc-completeness check threw: {ex.Message}")); }

try { CheckParserNameRegistryParity(repoRoot, warnings); }
catch (Exception ex) { warnings.Add(FormatWarning(repoRoot, "src/Tosh.Language/Parsing/ToshParser.cs", "TOSH901", $"parser-name parity check threw: {ex.Message}")); }

try { CheckOperatorParity(repoRoot, warnings); }
catch (Exception ex) { warnings.Add(FormatWarning(repoRoot, "src/Tosh.Runtime/OperatorEvaluator.cs", "TOSH902", $"operator parity check threw: {ex.Message}")); }

foreach (var w in warnings) Console.WriteLine(w);

// Always exit 0; the MSBuild target runs us with IgnoreExitCode="true" anyway,
// but being explicit avoids accidental build-fail wiring.
return 0;

static string? LocateRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Tosh.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}

static string FormatWarning(string repoRoot, string relativePath, string code, string message)
{
    // MSBuild canonical: origin(line,col): warning CODE: message
    var path = Path.Combine(repoRoot, relativePath).Replace('\\', '/');
    return $"{path}(1,1): warning {code}: {message}";
}

static string ResolveSourceHint(string repoRoot, Type type)
{
    var asm = type.Assembly.GetName().Name ?? "Tosh.Runtime";
    foreach (var sub in new[] { "Commands", "" })
    {
        var rel = string.IsNullOrEmpty(sub)
            ? $"src/{asm}/{type.Name}.cs"
            : $"src/{asm}/{sub}/{type.Name}.cs";
        if (File.Exists(Path.Combine(repoRoot, rel))) return rel;
    }
    return $"src/{asm}/{type.Name}.cs";
}

static void CheckDocumentationCompleteness(string repoRoot, List<string> warnings)
{
    var engine = new ToshEngine();
    foreach (var command in engine.Runtime.Commands.All.OfType<ShellCommand>())
    {
        var type = command.GetType();
        var optOuts = type.GetCustomAttributes<UndocumentedForAttribute>(inherit: false).ToList();
        var meta = command.GetMetadata();

        // Resolve a source-file path for the type when possible (for nicer diagnostics).
        var sourceHint = ResolveSourceHint(repoRoot, type);

        var explicitCategory = type.GetCustomAttribute<CommandCategoryAttribute>();
        if (explicitCategory is null && string.Equals(meta.Category, "Shell", StringComparison.OrdinalIgnoreCase)
            && !optOuts.Any(a => string.Equals(a.Field, "category", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(FormatWarning(repoRoot, sourceHint, "TOSH001",
                $"command '{command.Name}' ({type.Name}) lacks an explicit [CommandCategory] attribute."));
        }

        if (meta.Examples.Count == 0
            && !optOuts.Any(a => string.Equals(a.Field, "example", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(FormatWarning(repoRoot, sourceHint, "TOSH002",
                $"command '{command.Name}' ({type.Name}) has no [CommandExample] entries."));
        }

        if (string.IsNullOrWhiteSpace(meta.Output)
            && !optOuts.Any(a => string.Equals(a.Field, "output", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(FormatWarning(repoRoot, sourceHint, "TOSH003",
                $"command '{command.Name}' ({type.Name}) has no [CommandOutput] description."));
        }
    }
}

static void CheckParserNameRegistryParity(string repoRoot, List<string> warnings)
{
    var parserPath = Path.Combine(repoRoot, "src/Tosh.Language/Parsing/ToshParser.cs");
    if (!File.Exists(parserPath)) return;

    var source = File.ReadAllText(parserPath);
    var pattern = new Regex(
        @"string\.Equals\(\s*(?:commandName|nameToken\.Text)\s*,\s*""([a-z][a-z0-9\-]*)""",
        RegexOptions.IgnoreCase);

    var hardcoded = pattern.Matches(source)
        .Select(m => m.Groups[1].Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var engine = new ToshEngine();
    foreach (var name in hardcoded)
    {
        if (!engine.Runtime.Commands.TryGet(name, out _))
        {
            warnings.Add(FormatWarning(repoRoot, "src/Tosh.Language/Parsing/ToshParser.cs", "TOSH004",
                $"parser hardcodes command name '{name}' which does not resolve via the registry."));
        }
    }
}

static void CheckOperatorParity(string repoRoot, List<string> warnings)
{
    var evaluatorPath = Path.Combine(repoRoot, "src/Tosh.Runtime/OperatorEvaluator.cs");
    if (!File.Exists(evaluatorPath)) return;

    var source = File.ReadAllText(evaluatorPath);
    var binary = ExtractSwitchKeys(source, "EvaluateBinary");
    var matches = ExtractSwitchKeys(source, "Matches");

    var binarySet = binary.ToHashSet(StringComparer.Ordinal);
    foreach (var m in matches)
    {
        if (!binarySet.Contains(m))
        {
            warnings.Add(FormatWarning(repoRoot, "src/Tosh.Runtime/OperatorEvaluator.cs", "TOSH005",
                $"Match-only operator '{m}' is not present in EvaluateBinary."));
        }
    }
}

static List<string> ExtractSwitchKeys(string source, string methodName)
{
    var marker = methodName + "(";
    var idx = source.IndexOf(marker, StringComparison.Ordinal);
    if (idx < 0) return new();
    var i = source.IndexOf('{', idx);
    if (i < 0) return new();
    var depth = 0; var start = i;
    for (; i < source.Length; i++)
    {
        if (source[i] == '{') depth++;
        else if (source[i] == '}') { depth--; if (depth == 0) break; }
    }
    var body = source.Substring(start, i - start + 1);

    var ops = new List<string>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (Match m in Regex.Matches(body, "\"([^\"\\\\]+)\"\\s*=>"))
    {
        var key = m.Groups[1].Value;
        if (seen.Add(key)) ops.Add(key);
    }
    return ops;
}
