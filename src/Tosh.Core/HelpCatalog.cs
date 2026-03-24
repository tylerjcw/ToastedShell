using System.Text;

namespace Tosh.Core;

public static class HelpCatalog
{
    private static readonly IReadOnlyDictionary<string, LanguageHelpDefinition> LanguageTopics =
        new Dictionary<string, LanguageHelpDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["alias"] = new(
                Category: "Language",
                Description: "Defines a user alias that expands to a Tosh command pipeline.",
                Usage: "alias <name> = <pipeline>",
                Aliases: Array.Empty<string>(),
                Related: ["def", "source", "which", "unalias"],
                Examples:
                [
                    "alias ll = ls -la",
                    "alias recent = ls -la | where Modified > ((date now) - (timespan 2d))",
                ],
                Notes: "Aliases expand at invocation time and behave like shell commands."),
            ["def"] = new(
                Category: "Language",
                Description: "Defines a user function with optional CLR type annotations.",
                Usage: "def <name>(<param[: Type]> ...) [-> Type] { <statements> }",
                Aliases: Array.Empty<string>(),
                Related: ["alias", "return", "source", "undef"],
                Examples:
                [
                    "def recent(days: TimeSpan) { ls -la | where Modified > ((date now) - $days) }",
                    "def stringifyCount() -> string { count }",
                ],
                Notes: "Function parameters are dynamic by default, but can opt into CLR types."),
            ["var"] = new(
                Category: "Language",
                Description: "Declares a variable and stores the resulting CLR object without flattening it.",
                Usage: "var <name> = <expression-or-pipeline>",
                Aliases: ["set"],
                Related: ["set", "return", "using", "def"],
                Examples:
                [
                    "var files = ls -la",
                    "var rng = new System.Random()",
                ],
                Notes: "Use $name to reference a variable later in a command or expression."),
            ["set"] = new(
                Category: "Language",
                Description: "Alias of var for creating or updating a variable with shell-friendly wording.",
                Usage: "set <name> = <expression-or-pipeline>",
                Aliases: ["var"],
                Related: ["var", "unset", "export"],
                Examples:
                [
                    "set greeting = \"hello\"",
                    "set cutoff = ((date now) - (timespan 2d))",
                ],
                Notes: "Tosh currently treats set as the clearer alias of var."),
            ["using"] = new(
                Category: "Language",
                Description: "Imports CLR namespaces or loads Tosh modules/files into the current session.",
                Usage: "using <namespace> | using <namespace> as <alias> | using <path-to-file>",
                Aliases: Array.Empty<string>(),
                Related: ["source", "load-assembly", "types", "help"],
                Examples:
                [
                    "using System.IO",
                    "using System.IO = IO",
                    "using ~/.config/tosh/profile.tosh",
                ],
                Notes: "Namespace aliases can appear anywhere in a script. File-based using loads a module once per session."),
            ["if"] = new(
                Category: "Control Flow",
                Description: "Evaluates a condition and runs one block, optionally followed by else if or else blocks.",
                Usage: "if (<condition>) { <statements> } [else if (<condition>) { ... }] [else { ... }]",
                Aliases: Array.Empty<string>(),
                Related: ["where", "while", "for", "return"],
                Examples:
                [
                    "if ((ps | count) > 0) { writeline \"processes visible\" } else { writeline \"none\" }",
                ],
                Notes: "Conditions use the same expression and operator model as the rest of Tosh."),
            ["for"] = new(
                Category: "Control Flow",
                Description: "Iterates over an enumerable value and binds each item to a loop variable.",
                Usage: "for <name> in (<expression-or-pipeline>) { <statements> }",
                Aliases: Array.Empty<string>(),
                Related: ["each", "while", "break", "continue"],
                Examples:
                [
                    "for proc in (ps | sort Memory | reverse | first 3) { writeline $proc.Name }",
                ],
                Notes: "Loop variables are scoped to the block."),
            ["while"] = new(
                Category: "Control Flow",
                Description: "Runs a block repeatedly while a condition evaluates to true.",
                Usage: "while (<condition>) { <statements> }",
                Aliases: Array.Empty<string>(),
                Related: ["for", "if", "break", "continue"],
                Examples:
                [
                    "while (($count < 3)) { writeline $count; count = ($count + 1) }",
                ],
                Notes: "Use break and continue to control loop flow."),
            ["return"] = new(
                Category: "Control Flow",
                Description: "Exits the current function or top-level script early, optionally yielding a value.",
                Usage: "return [<expression-or-command>]",
                Aliases: Array.Empty<string>(),
                Related: ["def", "break", "continue", "if"],
                Examples:
                [
                    "return",
                    "return get Name",
                    "return String.Join(\" \", [\"hello\", \"world\"])",
                ],
                Notes: "Return can bubble out from nested blocks and loops."),
            ["break"] = new(
                Category: "Control Flow",
                Description: "Exits the current loop immediately.",
                Usage: "break",
                Aliases: Array.Empty<string>(),
                Related: ["continue", "for", "while", "each"],
                Examples:
                [
                    "for item in (echo one two three) { echo $item; break }",
                ],
                Notes: "Break currently applies inside for, while, and each-driven block execution."),
            ["continue"] = new(
                Category: "Control Flow",
                Description: "Skips the rest of the current loop iteration and continues with the next one.",
                Usage: "continue",
                Aliases: Array.Empty<string>(),
                Related: ["break", "for", "while", "each"],
                Examples:
                [
                    "echo one skip two | each { if (($it == skip)) { continue }; echo $it }",
                ],
                Notes: "Continue works in loops and each blocks."),
        };

    private static readonly IReadOnlyDictionary<string, string[]> ExamplesByName =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["help"] = ["help search json", "help ls", "help related where"],
            ["man"] = ["man ls", "man alias"],
            ["apropos"] = ["apropos json", "apropos loop"],
            ["ls"] = ["ls -la", "ls -la | where Size >= 1kb | get { Name, Size }"],
            ["ps"] = ["ps | sort Memory | reverse | first 5", "ps | get { Name, PID, Memory }"],
            ["where"] = ["ls -la | where Type == file", "ls -la | where Name.ToLower().EndsWith(\".md\")"],
            ["each"] = ["echo one two | each { $it.ToUpper() }", "DriveInfo.GetDrives() | each { $it }"],
            ["get"] = ["ls -la | get Name", "ps | get { Name, PID, Memory }"],
            ["sort"] = ["ps | sort Memory", "ls -la | sort Modified | reverse"],
            ["inspect"] = ["ls -la | first | inspect", "new System.Random() | inspect"],
            ["from-json"] = ["echo \"{\\\"name\\\":\\\"toast\\\"}\" | from-json", "curl https://example/api | from-json | flatten"],
            ["parse"] = ["ping -c 3 localhost | parse \"time=(?<time_ms>[0-9.]+) ms\"", "echo \"PID=42\" | parse \"PID=(?<Pid>[0-9]+)\""],
            ["types"] = ["types System.String", "types Random | first 5"],
            ["members"] = ["members string", "DateTime.Now | members"],
            ["new"] = ["var rng = new System.Random()", "new System.Text.StringBuilder(\"hello\").Append(\" world\").ToString()"],
        };

    public static IReadOnlyList<HelpSummary> BuildSummaries(ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        return BuildStaticTopics(runtime)
            .OrderBy(topic => topic.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(topic => topic.Name, StringComparer.OrdinalIgnoreCase)
            .Select(topic => new HelpSummary(
                topic.Name,
                topic.Kind,
                topic.Category,
                topic.Description,
                topic.Usage,
                topic.Aliases))
            .ToArray();
    }

    public static IReadOnlyList<HelpCategoryInfo> BuildCategories(ToshRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        return BuildStaticTopics(runtime)
            .GroupBy(topic => topic.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HelpCategoryInfo(group.Key, group.Count()))
            .ToArray();
    }

    public static HelpTopic? ResolveTopic(ToshRuntime runtime, string name)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var topics = BuildStaticTopicIndex(runtime);

        if (topics.TryGetValue(name, out var topic))
        {
            return topic;
        }

        var type = runtime.TypeResolver.Resolve(name);

        if (type is not null)
        {
            return CreateTypeTopic(type);
        }

        var external = ExternalCommandResolver.Resolve(runtime.CurrentDirectory, name);

        return external.Status == ExternalCommandLookupStatus.Found && external.ResolvedPath is not null
            ? CreateExternalTopic(name, external.ResolvedPath)
            : null;
    }

    public static IReadOnlyList<HelpSearchResult> Search(ToshRuntime runtime, string query, int maxResults = 12)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        return BuildStaticTopics(runtime)
            .Select(topic => new { Topic = topic, Score = ScoreTopic(topic, query) })
            .Where(result => result.Score > 0.0d)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Topic.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(result => new HelpSearchResult(
                result.Topic.Name,
                Math.Round(result.Score, 1),
                result.Topic.Kind,
                result.Topic.Category,
                result.Topic.Description,
                result.Topic.Usage,
                result.Topic.Aliases))
            .ToArray();
    }

    public static IReadOnlyList<HelpSearchResult> GetRelated(ToshRuntime runtime, string name, int maxResults = 6)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var topicIndex = BuildStaticTopicIndex(runtime);

        if (!topicIndex.TryGetValue(name, out var topic))
        {
            var resolved = ResolveTopic(runtime, name);

            if (resolved is null)
            {
                return Array.Empty<HelpSearchResult>();
            }

            return resolved.Related
                .Where(topicIndex.ContainsKey)
                .Take(maxResults)
                .Select((relatedName, index) => ToSearchResult(topicIndex[relatedName], 100.0d - (index * 5.0d)))
                .ToArray();
        }

        return topic.Related
            .Where(topicIndex.ContainsKey)
            .Take(maxResults)
            .Select((relatedName, index) => ToSearchResult(topicIndex[relatedName], 100.0d - (index * 5.0d)))
            .ToArray();
    }

    private static Dictionary<string, HelpTopic> BuildStaticTopicIndex(ToshRuntime runtime)
    {
        return BuildStaticTopics(runtime)
            .ToDictionary(topic => topic.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<HelpTopic> BuildStaticTopics(ToshRuntime runtime)
    {
        var commands = runtime.Commands.All.ToArray();
        var aliasMap = BuildBuiltInAliasMap(commands);
        var topics = new List<HelpTopic>(commands.Length + LanguageTopics.Count);

        foreach (var command in commands)
        {
            var kind = ToHelpSubjectKind(command);
            aliasMap.TryGetValue(command.Name, out var aliases);
            topics.Add(new HelpTopic(
                Name: command.Name,
                Kind: kind,
                Category: DetermineCommandCategory(command.Name, kind),
                Description: command.Description,
                Usage: command.Usage,
                Aliases: aliases ?? Array.Empty<string>(),
                Related: Array.Empty<string>(),
                Examples: ExamplesByName.TryGetValue(command.Name, out var examples) ? examples : Array.Empty<string>(),
                Path: null,
                Notes: GetCommandNotes(command.Name)));
        }

        foreach (var (name, definition) in LanguageTopics)
        {
            topics.Add(new HelpTopic(
                Name: name,
                Kind: HelpSubjectKind.Language,
                Category: definition.Category,
                Description: definition.Description,
                Usage: definition.Usage,
                Aliases: definition.Aliases,
                Related: definition.Related,
                Examples: definition.Examples,
                Path: null,
                Notes: definition.Notes));
        }

        return AddRelatedTopics(topics);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildBuiltInAliasMap(IEnumerable<IShellCommand> commands)
    {
        var groups = commands
            .Where(command => ToHelpSubjectKind(command) == HelpSubjectKind.BuiltIn)
            .GroupBy(BuildAliasKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();

        var aliasMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var names = group
                .Select(command => command.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var name in names)
            {
                aliasMap[name] = names
                    .Where(candidate => !string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
        }

        return aliasMap;
    }

    private static string BuildAliasKey(IShellCommand command)
    {
        return string.Join(
            "|",
            command.GetType().FullName,
            command.Description,
            NormalizeUsage(command));
    }

    private static string NormalizeUsage(IShellCommand command)
    {
        return command.Usage.StartsWith(command.Name, StringComparison.OrdinalIgnoreCase)
            ? "<name>" + command.Usage[command.Name.Length..]
            : command.Usage;
    }

    private static IReadOnlyList<HelpTopic> AddRelatedTopics(IReadOnlyList<HelpTopic> topics)
    {
        return topics
            .Select(topic => topic with
            {
                Related = topic.Related
                    .Concat(
                        topics
                    .Where(candidate => !string.Equals(candidate.Name, topic.Name, StringComparison.OrdinalIgnoreCase))
                    .Where(candidate => !topic.Aliases.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
                    .Where(candidate => !candidate.Aliases.Contains(topic.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(candidate => new { candidate.Name, Score = ScoreRelated(topic, candidate) })
                    .Where(result => result.Score > 0.0d)
                    .OrderByDescending(result => result.Score)
                    .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(result => result.Name)
                    .Take(8))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToArray(),
            })
            .ToArray();
    }

    private static double ScoreTopic(HelpTopic topic, string query)
    {
        var trimmed = query.Trim();

        if (trimmed.Length == 0)
        {
            return 0.0d;
        }

        double score = 0.0d;

        if (string.Equals(topic.Name, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 120.0d);
        }

        if (topic.Aliases.Any(alias => string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 116.0d);
        }

        if (topic.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score = Math.Max(score, 96.0d);
        }

        if (topic.Aliases.Any(alias => alias.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            score = Math.Max(score, 90.0d);
        }

        if (topic.Category.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score += 24.0d;
        }

        if (topic.Description.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score += 34.0d;
        }

        if (topic.Usage.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score += 28.0d;
        }

        if (!string.IsNullOrWhiteSpace(topic.Notes) &&
            topic.Notes.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            score += 18.0d;
        }

        var fuzzyNameScore = HelpFuzzyMatcher.FuzzyMatch(topic.Name, trimmed);
        var fuzzyAliasScore = topic.Aliases.Select(alias => HelpFuzzyMatcher.FuzzyMatch(alias, trimmed)).DefaultIfEmpty(0.0d).Max();

        if (fuzzyNameScore >= 0.55d)
        {
            score = Math.Max(score, fuzzyNameScore * 88.0d);
        }

        if (fuzzyAliasScore >= 0.55d)
        {
            score = Math.Max(score, fuzzyAliasScore * 80.0d);
        }

        var queryTokens = ExtractTokens(trimmed);
        var topicTokens = ExtractTokens(topic.Name, topic.Category, topic.Description, topic.Usage, topic.Notes ?? string.Empty, string.Join(' ', topic.Aliases));
        score += queryTokens.Count(token => topicTokens.Contains(token)) * 6.0d;

        return score;
    }

    private static double ScoreRelated(HelpTopic left, HelpTopic right)
    {
        double score = 0.0d;

        if (string.Equals(left.Category, right.Category, StringComparison.OrdinalIgnoreCase))
        {
            score += 40.0d;
        }

        if (left.Kind == right.Kind)
        {
            score += 8.0d;
        }

        var leftTokens = ExtractTokens(left.Name, left.Description, left.Usage, left.Notes ?? string.Empty);
        var rightTokens = ExtractTokens(right.Name, right.Description, right.Usage, right.Notes ?? string.Empty);
        score += leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count() * 8.0d;

        return score;
    }

    private static HelpSearchResult ToSearchResult(HelpTopic topic, double score)
    {
        return new HelpSearchResult(
            topic.Name,
            Math.Round(score, 1),
            topic.Kind,
            topic.Category,
            topic.Description,
            topic.Usage,
            topic.Aliases);
    }

    private static HelpSubjectKind ToHelpSubjectKind(IShellCommand command)
    {
        return command is ICommandResolutionMetadata metadata
            ? metadata.ResolutionKind switch
            {
                CommandResolutionKind.BuiltIn => HelpSubjectKind.BuiltIn,
                CommandResolutionKind.Alias => HelpSubjectKind.Alias,
                CommandResolutionKind.Function => HelpSubjectKind.Function,
                CommandResolutionKind.External => HelpSubjectKind.External,
                _ => HelpSubjectKind.BuiltIn,
            }
            : HelpSubjectKind.BuiltIn;
    }

    private static string DetermineCommandCategory(string name, HelpSubjectKind kind)
    {
        if (kind is HelpSubjectKind.Alias or HelpSubjectKind.Function)
        {
            return "Scripting";
        }

        return name switch
        {
            "help" or "man" or "apropos" or "history" or "history-search" or "view" or "clear" or "exit" or "which" or "whence"
                => "Shell",
            "write" or "writeline" or "echo" or "head" or "tail" or "wc" or "uniq" or "cut" or "tr" or "grep" or "split" or "join-lines" or "replace" or "match" or "template"
                => "Text",
            "pwd" or "cd" or "ls" or "df" or "mounts" or "du" or "usage" or "disk-usage" or "stat" or "find" or "cat" or "mkdir" or "touch" or "rm" or "cp" or "mv" or "chmod" or "chown" or "ln" or "readlink" or "realpath" or "dirname" or "basename" or "exists" or "is-file" or "is-dir" or "is-link" or "mkdir-temp" or "tempfile"
                => "Filesystem",
            "ps" => "Process",
            "ping" => "Network",
            "uname" or "hostname" or "whoami" or "id" or "env" or "free" or "uptime" or "sleep" or "seq" or "date" or "timespan" or "export" or "unset"
                => "System",
            "get" or "select" or "pick" or "rename" or "inspect" or "where" or "each" or "first" or "last" or "skip" or "sort" or "sort-by" or "reverse" or "count" or "flatten" or "distinct" or "group-by" or "take-while" or "skip-while" or "tee" or "sum" or "average" or "avg" or "min" or "max" or "xargs"
                => "Pipeline",
            "from-json" or "from-csv" or "from-xml" or "parse" or "to-json" or "to-csv" or "hash"
                => "Data",
            "type-of" or "describe-type" or "members" or "methods" or "constructors" or "types" or "load-assembly" or "cast" or "new" or "call"
                => "CLR",
            _ => "Shell",
        };
    }

    private static string? GetCommandNotes(string name)
    {
        return name switch
        {
            "help" or "man" => "Use `help search <query>` or `apropos <query>` to find commands and language topics quickly.",
            "apropos" => "Apropos performs fuzzy help search across commands and Tosh language topics.",
            "where" => "Inside predicate expressions, bare member access resolves against the current pipeline object.",
            "each" => "Collections stay intact until you explicitly expand them with `each` or `flatten`.",
            "inspect" => "Inspect shows CLR type, assembly, interfaces, and member previews for pipeline values.",
            "parse" => "Parse currently supports regex extraction with named capture groups.",
            "from-json" or "from-csv" or "from-xml" => "Structured input commands keep parsed values as CLR objects until you explicitly flatten them.",
            "ps" => "Process memory values are surfaced as Tosh StorageSize objects, not raw strings.",
            "ls" => "Filesystem metadata stays typed in the pipeline, even when Tosh renders it like a shell table.",
            "new" => "Tosh supports both the legacy `new <Type> ...` command form and the newer C#-style `new Type(...)` expression syntax.",
            _ => null,
        };
    }

    private static HelpTopic CreateTypeTopic(Type type)
    {
        var typeName = ReflectionMetadataUtilities.GetDisplayName(type);
        var constructors = type.GetConstructors().Length;
        var methods = type.GetMethods().Length;
        var members = type.GetMembers().Length;

        return new HelpTopic(
            Name: type.Name,
            Kind: HelpSubjectKind.Type,
            Category: "CLR",
            Description: $"CLR type {typeName} from assembly {type.Assembly.GetName().Name}.",
            Usage: $"new {typeName}(...)",
            Aliases: Array.Empty<string>(),
            Related: ["describe-type", "members", "methods", "constructors", "cast", "new"],
            Examples:
            [
                $"describe-type {typeName}",
                $"methods {typeName} | first 10",
            ],
            Path: type.Assembly.GetName().Name,
            Notes: $"Namespace: {type.Namespace ?? "<global>"} | Base: {ReflectionMetadataUtilities.GetDisplayName(type.BaseType ?? typeof(object))} | Members: {members} | Methods: {methods} | Constructors: {constructors}");
    }

    private static HelpTopic CreateExternalTopic(string name, string path)
    {
        return new HelpTopic(
            Name: name,
            Kind: HelpSubjectKind.External,
            Category: "External",
            Description: "External executable resolved from PATH or an explicit executable path.",
            Usage: $"{name} [args ...]",
            Aliases: Array.Empty<string>(),
            Related: ["which", "parse", "from-json", "from-csv", "from-xml", "xargs"],
            Examples: Array.Empty<string>(),
            Path: path,
            Notes: "External stdout displays as plain text by default and can be adapted into objects with parse/from-* commands.");
    }

    private static HashSet<string> ExtractTokens(params string[] values)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                    continue;
                }

                FlushToken(builder, tokens);
            }

            FlushToken(builder, tokens);
        }

        return tokens;
    }

    private static void FlushToken(StringBuilder builder, ISet<string> tokens)
    {
        if (builder.Length <= 1)
        {
            builder.Clear();
            return;
        }

        tokens.Add(builder.ToString());
        builder.Clear();
    }

    private sealed record LanguageHelpDefinition(
        string Category,
        string Description,
        string Usage,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> Related,
        IReadOnlyList<string> Examples,
        string? Notes);
}
