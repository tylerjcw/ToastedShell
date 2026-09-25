using System.Dynamic;
using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandArgument("subcommand-or-type", "Optional subcommand keyword (`has`, `get`, `props`, `fields`, `methods`, `events`, `add`, `del`) or a type name.", Required = false)]
[CommandArgument("name", "Operand for `has`/`get`: the member name to look up.", Required = false)]
[CommandArgument("args ...", "Optional additional arguments, names for `del`, or values and flags for `add`.", Required = false)]
[CommandExample("members string", Title = "List all members of String")]
[CommandExample("DateTime.Now | members", Title = "List all members of a piped object")]
[CommandExample("$obj | members has Name", Title = "Check whether $obj has a member named 'Name'")]
[CommandExample("$obj | members get FullName", Title = "Return the descriptor for member 'FullName' (or null)")]
[CommandExample("$obj | members props", Title = "Filter to properties only")]
[CommandExample("$obj | members fields", Title = "Filter to fields only")]
[CommandExample("$obj | members methods", Title = "Filter to methods only")]
[CommandExample("$obj | members add \"Key\" \"value\"", Title = "Add a dynamic property to a fluid object")]
[CommandExample("$obj | members add { prop Name: string = \"value\" }", Title = "Add properties using declaration block")]
[CommandExample("$obj | members del Key", Title = "Delete a dynamic property from an object")]
[CommandOutput("Member descriptor objects with Name, Kind, MemberType, and other reflection metadata.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Inspects the type of each piped object.")]
public sealed class MembersCommand : ShellCommand
{
    public MembersCommand()
        : base("members", "Lists or queries members for CLR types or pipeline objects.", "members [has|get|props|fields|methods|events|add|del] [name] | members [type ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (IntrospectionSubcommands.TryDispatch(context, out var subcommand, out var operand))
        {
            await foreach (var result in ExecuteSubcommandAsync(context, subcommand!, operand))
            {
                yield return result;
            }

            yield break;
        }

        var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        var types = ResolveTargets(context.Arguments, input, context);

        foreach (var type in types)
        {
            foreach (var member in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
            {
                yield return member;
            }
        }
    }

    private static IReadOnlyList<object> ResolveTargets(IReadOnlyList<object?> arguments, IReadOnlyList<object?> input, CommandContext context)
    {
        if (arguments.Count > 0)
        {
            return ReflectionMetadataUtilities.ResolveTypeLikeTargets(context, arguments);
        }

        return input
            .Select(item => ReflectionMetadataUtilities.ResolveTypeLikeTarget(context, item ?? typeof(object)))
            .DistinctBy(type => type is Type clrType
                ? clrType.AssemblyQualifiedName ?? clrType.FullName ?? clrType.Name
                : ((IShellTypeDescriptor)type).ShellFullName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async IAsyncEnumerable<object?> ExecuteSubcommandAsync(
        CommandContext context,
        string subcommand,
        string? operand)
    {
        var input = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        switch (subcommand)
        {
            case "add":
                await foreach (var item in ExecuteAddAsync(context, input))
                {
                    yield return item;
                }
                yield break;

            case "del":
            case "delete":
                await foreach (var item in ExecuteDelAsync(context, input))
                {
                    yield return item;
                }
                yield break;
        }

        var skip = operand is null ? 1 : 2;
        var trailing = context.Arguments.Skip(skip).ToArray();
        var types = ResolveTargets(trailing, input, context);

        switch (subcommand)
        {
            case "has":
                if (operand is null)
                {
                    throw new InvalidOperationException("members has: requires a member name.");
                }

                yield return AnyMember(types, operand, includeMethods: true);
                yield break;

            case "get":
                if (operand is null)
                {
                    throw new InvalidOperationException("members get: requires a member name.");
                }

                foreach (var match in FindMembers(types, operand, includeMethods: true))
                {
                    yield return match;
                }

                yield break;

            case "props":
                foreach (var type in types)
                {
                    foreach (var m in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
                    {
                        if (KindOf(m) is "Property")
                        {
                            yield return m;
                        }
                    }
                }

                yield break;

            case "fields":
                foreach (var type in types)
                {
                    foreach (var m in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
                    {
                        if (KindOf(m) is "Field")
                        {
                            yield return m;
                        }
                    }
                }

                yield break;

            case "methods":
                foreach (var type in types)
                {
                    foreach (var m in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
                    {
                        yield return m;
                    }
                }

                yield break;

            case "events":
                yield break;

            default:
                throw new InvalidOperationException($"members: unknown subcommand '{subcommand}'.");
        }
    }

    private static bool AnyMember(IReadOnlyList<object> types, string name, bool includeMethods)
    {
        foreach (var type in types)
        {
            foreach (var m in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
            {
                if (NameOf(m).Equals(name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            if (!includeMethods)
            {
                continue;
            }

            foreach (var m in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
            {
                if (NameOf(m).Equals(name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<ExpandoObject> FindMembers(IReadOnlyList<object> types, string name, bool includeMethods)
    {
        foreach (var type in types)
        {
            foreach (var m in ReflectionMetadataUtilities.EnumerateMemberProjections(type))
            {
                if (NameOf(m).Equals(name, StringComparison.Ordinal))
                {
                    yield return m;
                }
            }

            if (!includeMethods)
            {
                continue;
            }

            foreach (var m in ReflectionMetadataUtilities.EnumerateMethodProjections(type))
            {
                if (NameOf(m).Equals(name, StringComparison.Ordinal))
                {
                    yield return m;
                }
            }
        }
    }

    private static string KindOf(ExpandoObject record)
    {
        var dict = (IDictionary<string, object?>)record;
        return dict.TryGetValue("Kind", out var v) ? (v as string ?? string.Empty) : string.Empty;
    }

    private static string NameOf(ExpandoObject record)
    {
        var dict = (IDictionary<string, object?>)record;
        return dict.TryGetValue("Name", out var v) ? (v as string ?? string.Empty) : string.Empty;
    }

    private static async IAsyncEnumerable<object?> ExecuteAddAsync(
        CommandContext context,
        IReadOnlyList<object?> input)
    {
        IReadOnlyList<object?> targets;
        IReadOnlyList<object?> args;

        if (input.Count > 0)
        {
            targets = input;
            args = context.Arguments.Skip(1).ToArray();
        }
        else if (context.Arguments.Count > 1 && (context.Arguments[1] is ToshClassInstance or ToshRecordInstance or ExpandoObject or IDictionary<string, object?>))
        {
            targets = [context.Arguments[1]];
            args = context.Arguments.Skip(2).ToArray();
        }
        else
        {
            throw new InvalidOperationException("'members add' requires a target object from the pipeline or as an argument.");
        }

        if (args.Count == 0)
        {
            throw new InvalidOperationException("'members add' requires property definitions (block or name and options).");
        }

        var firstArg = args[0];
        var isBlockStyle = firstArg is ShellBlock || (firstArg is string s && s.TrimStart().StartsWith('{'));

        if (isBlockStyle)
        {
            IReadOnlyList<ClassMemberSyntax> members;
            string sourceName = "<dynamic>";
            string? sourceText = null;

            if (firstArg is ShellBlock shellBlock)
            {
                sourceName = shellBlock.SourceName;
                sourceText = shellBlock.SourceText;

                if (shellBlock.Syntax is BlockSyntax blockSyntax)
                {
                    var memberList = new List<ClassMemberSyntax>();
                    foreach (var stmt in blockSyntax.Statements)
                    {
                        if (stmt is PropertyDeclarationStatementSyntax propStmt)
                        {
                            memberList.Add(propStmt.Property);
                        }
                        else
                        {
                            throw new InvalidOperationException("Only property definitions ('prop') are supported in 'members add' blocks.");
                        }
                    }
                    members = memberList;
                }
                else
                {
                    var blockText = shellBlock.SourceText.Substring(shellBlock.Span.Start, shellBlock.Span.Length);
                    var (parsedMembers, diagnostics) = ToshParser.ParseClassMembersBlock(blockText, shellBlock.SourceName, shellBlock.Span.Start, shellBlock.SourceText);
                    if (diagnostics.Count > 0)
                    {
                        throw new InvalidOperationException($"Syntax error in 'members add' block: {diagnostics[0].Title}");
                    }
                    members = parsedMembers;
                }
            }
            else
            {
                var blockText = (string)firstArg!;
                var (parsedMembers, diagnostics) = ToshParser.ParseClassMembersBlock(blockText, sourceName, 0, blockText);
                if (diagnostics.Count > 0)
                {
                    throw new InvalidOperationException($"Syntax error in 'members add' block: {diagnostics[0].Title}");
                }
                members = parsedMembers;
            }

            foreach (var member in members)
            {
                if (member is not ClassPropertyMemberSyntax prop)
                {
                    throw new InvalidOperationException("Only property definitions ('prop') are supported in 'members add' blocks.");
                }

                foreach (var target in targets)
                {
                    if (target is ToshClassInstance classInstance)
                    {
                        if (!classInstance.Definition.IsFluid)
                        {
                            throw context.CreateDiagnostic(
                                code: "tosh.runtime.members_add_requires_fluid",
                                title: $"Class '{classInstance.Definition.Name}' is not fluid. Declare the class with 'fluid' to allow dynamic member addition.",
                                label: "class is not fluid");
                        }

                        if (classInstance.Definition.TryGetDeclaredProperty(prop.Name, out _))
                        {
                            throw new InvalidOperationException($"Cannot add dynamic property '{prop.Name}' because it shadows a declared property on class '{classInstance.Definition.Name}'.");
                        }

                        object? initValue = null;
                        if (prop.GetterBody is null && prop.Initializer is not null)
                        {
                            initValue = await classInstance.Definition.EvaluateDynamicInitializerAsync(classInstance, prop.Initializer, context.CancellationToken, sourceName, sourceText);
                            if (!string.IsNullOrWhiteSpace(prop.TypeName))
                            {
                                initValue = await classInstance.Definition.ConvertDynamicPropertyValueAsync(classInstance, prop.TypeName, initValue, context.CancellationToken);
                            }
                        }

                        var desc = new DynamicPropertyDescriptor
                        {
                            Name = prop.Name,
                            TypeName = prop.TypeName,
                            Value = initValue,
                            Getter = prop.GetterBody,
                            Setter = prop.SetterBody,
                            IsShy = prop.IsShy,
                            IsFixed = prop.IsFixed,
                            SourceName = sourceName,
                            SourceText = sourceText,
                        };
                        classInstance.AddDynamicProperty(desc);
                    }
                    else if (target is ToshRecordInstance recordInstance)
                    {
                        if (!recordInstance.Definition.IsFluid)
                        {
                            throw context.CreateDiagnostic(
                                code: "tosh.runtime.members_add_requires_fluid",
                                title: $"Record '{recordInstance.Definition.Name}' is not fluid. Declare the record with 'fluid' to allow dynamic member addition.",
                                label: "record is not fluid");
                        }

                        if (recordInstance.Definition.TryGetField(prop.Name, out _))
                        {
                            throw new InvalidOperationException($"Cannot add dynamic property '{prop.Name}' because it shadows a declared field on record '{recordInstance.Definition.Name}'.");
                        }

                        if (prop.GetterBody is not null || prop.SetterBody is not null)
                        {
                            throw new InvalidOperationException("Computed properties are only supported on class instances, not records.");
                        }

                        object? initValue = null;
                        if (prop.Initializer is not null)
                        {
                            initValue = await recordInstance.Definition.EvaluateDynamicInitializerAsync(prop.Initializer, context.CancellationToken, sourceName, sourceText);
                        }

                        recordInstance.SetStoredValue(prop.Name, initValue);
                    }
                    else if (target is ExpandoObject expando)
                    {
                        ((IDictionary<string, object?>)expando)[prop.Name] = null;
                    }
                    else if (target is IDictionary<string, object?> dict)
                    {
                        dict[prop.Name] = null;
                    }
                    else
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.members_add_requires_fluid",
                            title: $"Type '{target?.GetType().Name ?? "null"}' is not fluid and does not allow dynamic member addition.",
                            label: "type is not fluid");
                    }
                }
            }
        }
        else
        {
            var name = firstArg?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Property name cannot be empty.");
            }

            string? typeName = null;
            object? getter = null;
            object? setter = null;
            var isShy = false;
            var isFixed = false;
            object? value = null;
            var hasExplicitValue = false;

            for (var i = 1; i < args.Count; i++)
            {
                var arg = args[i];
                if (arg is string opt && opt.StartsWith("-", StringComparison.Ordinal))
                {
                    switch (opt.ToLowerInvariant())
                    {
                        case "--type":
                            if (i + 1 >= args.Count) throw new InvalidOperationException("--type requires a type name argument.");
                            typeName = args[++i]?.ToString();
                            break;
                        case "--get":
                            if (i + 1 >= args.Count) throw new InvalidOperationException("--get requires a block or callable argument.");
                            getter = args[++i];
                            break;
                        case "--set":
                            if (i + 1 >= args.Count) throw new InvalidOperationException("--set requires a block or callable argument.");
                            setter = args[++i];
                            break;
                        case "--shy":
                        case "--private":
                            isShy = true;
                            break;
                        case "--fixed":
                        case "--readonly":
                            isFixed = true;
                            break;
                        default:
                            throw new InvalidOperationException($"Unknown option '{opt}' for 'members add'.");
                    }
                }
                else if (!hasExplicitValue)
                {
                    value = arg;
                    hasExplicitValue = true;
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected argument '{arg}' in 'members add'.");
                }
            }

            foreach (var target in targets)
            {
                if (target is ToshClassInstance classInstance)
                {
                    if (!classInstance.Definition.IsFluid)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.members_add_requires_fluid",
                            title: $"Class '{classInstance.Definition.Name}' is not fluid. Declare the class with 'fluid' to allow dynamic member addition.",
                            label: "class is not fluid");
                    }

                    if (classInstance.Definition.TryGetDeclaredProperty(name, out _))
                    {
                        throw new InvalidOperationException($"Cannot add dynamic property '{name}' because it shadows a declared property on class '{classInstance.Definition.Name}'.");
                    }

                    if (getter is null && typeName is not null && hasExplicitValue)
                    {
                        value = await classInstance.Definition.ConvertDynamicPropertyValueAsync(classInstance, typeName, value, context.CancellationToken);
                    }

                    var desc = new DynamicPropertyDescriptor
                    {
                        Name = name,
                        TypeName = typeName,
                        Value = value,
                        Getter = getter,
                        Setter = setter,
                        IsShy = isShy,
                        IsFixed = isFixed,
                    };
                    classInstance.AddDynamicProperty(desc);
                }
                else if (target is ToshRecordInstance recordInstance)
                {
                    if (!recordInstance.Definition.IsFluid)
                    {
                        throw context.CreateDiagnostic(
                            code: "tosh.runtime.members_add_requires_fluid",
                            title: $"Record '{recordInstance.Definition.Name}' is not fluid. Declare the record with 'fluid' to allow dynamic member addition.",
                            label: "record is not fluid");
                    }

                    if (recordInstance.Definition.TryGetField(name, out _))
                    {
                        throw new InvalidOperationException($"Cannot add dynamic property '{name}' because it shadows a declared field on record '{recordInstance.Definition.Name}'.");
                    }

                    if (getter is not null || setter is not null)
                    {
                        throw new InvalidOperationException("Computed properties are only supported on class instances, not records.");
                    }

                    recordInstance.SetStoredValue(name, value);
                }
                else if (target is ExpandoObject expando)
                {
                    ((IDictionary<string, object?>)expando)[name] = value;
                }
                else if (target is IDictionary<string, object?> dict)
                {
                    dict[name] = value;
                }
                else
                {
                    throw context.CreateDiagnostic(
                        code: "tosh.runtime.members_add_requires_fluid",
                        title: $"Type '{target?.GetType().Name ?? "null"}' is not fluid and does not allow dynamic member addition.",
                        label: "type is not fluid");
                }
            }
        }

        foreach (var target in targets)
        {
            yield return target;
        }
    }

    private static async IAsyncEnumerable<object?> ExecuteDelAsync(
        CommandContext context,
        IReadOnlyList<object?> input)
    {
        IReadOnlyList<object?> targets;
        IReadOnlyList<object?> args;

        if (input.Count > 0)
        {
            targets = input;
            args = context.Arguments.Skip(1).ToArray();
        }
        else if (context.Arguments.Count > 1 && (context.Arguments[1] is ToshClassInstance or ToshRecordInstance or ExpandoObject or IDictionary<string, object?>))
        {
            targets = [context.Arguments[1]];
            args = context.Arguments.Skip(2).ToArray();
        }
        else
        {
            throw new InvalidOperationException("'members del' requires a target object from the pipeline or as an argument.");
        }

        if (args.Count == 0)
        {
            throw new InvalidOperationException("'members del' requires at least one property name to delete.");
        }

        var namesToDelete = new List<string>();
        foreach (var arg in args)
        {
            if (arg is string s)
            {
                namesToDelete.Add(s);
            }
            else if (arg is ProjectedMemberSelection proj)
            {
                namesToDelete.AddRange(proj.MemberPaths);
            }
            else if (arg is ShellBlock block)
            {
                var text = block.SourceText.Substring(block.Span.Start, block.Span.Length).Trim();
                if (text.StartsWith('{') && text.EndsWith('}'))
                {
                    text = text[1..^1];
                }
                var parts = text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) namesToDelete.Add(trimmed);
                }
            }
            else if (arg is IEnumerable<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is not null) namesToDelete.Add(item.ToString()!);
                }
            }
        }

        foreach (var target in targets)
        {
            foreach (var name in namesToDelete)
            {
                if (target is ToshClassInstance classInstance)
                {
                    if (classInstance.Definition.TryGetDeclaredProperty(name, out _))
                    {
                        throw new InvalidOperationException($"Cannot delete declared property '{name}' on class '{classInstance.Definition.Name}'. Only dynamic properties can be removed.");
                    }

                    classInstance.RemoveDynamicProperty(name);
                    classInstance.RemoveStoredValue(name);
                }
                else if (target is ToshRecordInstance recordInstance)
                {
                    if (recordInstance.Definition.TryGetField(name, out _))
                    {
                        throw new InvalidOperationException($"Cannot delete declared field '{name}' on record '{recordInstance.Definition.Name}'. Only dynamic fields can be removed.");
                    }

                    recordInstance.RemoveStoredValue(name);
                }
                else if (target is ExpandoObject expando)
                {
                    ((IDictionary<string, object?>)expando).Remove(name);
                }
                else if (target is IDictionary<string, object?> dict)
                {
                    dict.Remove(name);
                }
            }

            yield return target;
        }
    }
}
