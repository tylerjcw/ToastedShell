using System.Dynamic;
using System.Reflection;
using System.Runtime.Loader;
using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

public sealed class DocumentSemanticModel
{
    private static readonly object AssemblyLoadSync = new();
    private static readonly HashSet<string> AttemptedAssemblyLoads = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _sourceName;
    private readonly IReadOnlyList<UsingDirective> _usingDirectives;
    private readonly IReadOnlyList<RequireDirective> _requireDirectives;
    private readonly IReadOnlyList<TypedBinding> _bindings;
    private readonly IReadOnlyList<ShellClassDeclaration> _classDeclarations;
    private readonly Dictionary<int, DotNetTypeResolver> _resolverCache = new();
    private readonly Dictionary<TypedBinding, Type?> _bindingTypeCache = new();
    private readonly Dictionary<TypedBinding, ShellClassSymbol?> _bindingShellClassCache = new();

    private DocumentSemanticModel(
        string sourceName,
        IReadOnlyList<UsingDirective> usingDirectives,
        IReadOnlyList<RequireDirective> requireDirectives,
        IReadOnlyList<TypedBinding> bindings,
        IReadOnlyList<ShellClassDeclaration> classDeclarations)
    {
        _sourceName = sourceName;
        _usingDirectives = usingDirectives;
        _requireDirectives = requireDirectives;
        _bindings = bindings;
        _classDeclarations = classDeclarations;
    }

    public static DocumentSemanticModel Create(string sourceName, string text)
    {
        var parseResult = ToshParser.Parse(text, sourceName);
        var collector = new Collector();
        collector.Collect(parseResult.Statement, TextSpan.FromBounds(0, parseResult.SourceText.Length), 0);
        return new DocumentSemanticModel(
            sourceName,
            collector.UsingDirectives,
            collector.RequireDirectives,
            collector.Bindings,
            collector.ClassDeclarations);
    }

    public DotNetTypeResolver CreateTypeResolver(int offset)
    {
        offset = Math.Max(0, offset);

        if (_resolverCache.TryGetValue(offset, out var cached))
        {
            return cached;
        }

        LoadVisibleRequiredAssemblies(offset);
        var resolver = new DotNetTypeResolver();

        foreach (var directive in _usingDirectives
                     .Where(directive => directive.SelectionStart <= offset)
                     .OrderBy(directive => directive.SelectionStart))
        {
            if (directive.Alias is null)
            {
                resolver.AddUsing(directive.Target);
            }
            else
            {
                resolver.AddAlias(directive.Alias, directive.Target);
            }
        }

        _resolverCache[offset] = resolver;
        return resolver;
    }

    public IReadOnlyList<string> GetVisibleImports(int offset)
    {
        return _usingDirectives
            .Where(directive => directive.Alias is null && directive.SelectionStart <= offset)
            .Select(directive => directive.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<KeyValuePair<string, string>> GetVisibleAliases(int offset)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directive in _usingDirectives
                     .Where(directive => directive.Alias is not null && directive.SelectionStart <= offset)
                     .OrderBy(directive => directive.SelectionStart))
        {
            aliases[directive.Alias!] = directive.Target;
        }

        return aliases
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Type? ResolveVisibleVariableType(int offset, string name)
    {
        var binding = ResolveVisibleBinding(offset, name);
        return binding is null ? null : ResolveBindingType(binding);
    }

    public ShellClassSymbol? ResolveVisibleShellClass(int offset, string name)
    {
        return _classDeclarations
            .Where(declaration =>
                string.Equals(declaration.Symbol.Name, name, StringComparison.Ordinal) &&
                declaration.ScopeStart <= offset &&
                offset <= declaration.ScopeEnd &&
                declaration.SelectionStart <= offset)
            .OrderByDescending(declaration => declaration.ScopeDepth)
            .ThenByDescending(declaration => declaration.SelectionStart)
            .Select(declaration => declaration.Symbol)
            .FirstOrDefault();
    }

    public ShellClassSymbol? ResolveVisibleVariableShellClass(int offset, string name)
    {
        var binding = ResolveVisibleBinding(offset, name);
        return binding is null ? null : ResolveBindingShellClass(binding);
    }

    public IReadOnlyList<ShellClassSymbol> GetVisibleShellClasses(int offset)
    {
        return _classDeclarations
            .Where(declaration =>
                declaration.ScopeStart <= offset &&
                offset <= declaration.ScopeEnd &&
                declaration.SelectionStart <= offset)
            .OrderByDescending(declaration => declaration.ScopeDepth)
            .ThenByDescending(declaration => declaration.SelectionStart)
            .Select(declaration => declaration.Symbol)
            .DistinctBy(symbol => symbol.Name, StringComparer.Ordinal)
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ShellReferenceSymbol? ResolveShellReference(int offset, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (reference.StartsWith("$", StringComparison.Ordinal))
        {
            var trimmed = reference[1..];
            var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length == 0)
            {
                return null;
            }

            var rootClass = ResolveVisibleVariableShellClass(offset, segments[0]);

            if (rootClass is null)
            {
                return null;
            }

            if (segments.Length == 1)
            {
                return new ShellReferenceSymbol.Class(rootClass);
            }

            return ResolveShellReferenceCore(
                offset,
                rootClass,
                segments[1..],
                isStaticContext: false,
                includeHidden: string.Equals(segments[0], "this", StringComparison.Ordinal));
        }

        var directClass = ResolveVisibleShellClass(offset, reference);

        if (directClass is not null)
        {
            return new ShellReferenceSymbol.Class(directClass);
        }

        var parts = reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
        {
            return null;
        }

        var rootShellClass = ResolveVisibleShellClass(offset, parts[0]);

        return rootShellClass is null
            ? null
            : ResolveShellReferenceCore(offset, rootShellClass, parts[1..], isStaticContext: true, includeHidden: false);
    }

    public ShellClassSymbol? ResolveShellTargetClass(int offset, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (reference.StartsWith("$", StringComparison.Ordinal))
        {
            var trimmed = reference[1..];
            var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length == 0)
            {
                return null;
            }

            var currentClass = ResolveVisibleVariableShellClass(offset, segments[0]);
            var includeHidden = string.Equals(segments[0], "this", StringComparison.Ordinal);

            if (currentClass is null)
            {
                return null;
            }

            foreach (var segment in segments.Skip(1))
            {
                var property = currentClass.Properties
                    .FirstOrDefault(candidate => string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase) &&
                                                 (includeHidden || !candidate.IsHidden));

                if (property is not null)
                {
                    currentClass = ResolveShellClassFromAnnotation(offset, property.TypeName);

                    if (currentClass is null)
                    {
                        return null;
                    }

                    continue;
                }

                var method = currentClass.Methods
                    .Where(candidate => !candidate.IsStatic &&
                                        (includeHidden || !candidate.IsHidden) &&
                                        string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(candidate => candidate.Parameters.Count)
                    .FirstOrDefault();

                if (method is null)
                {
                    return null;
                }

                currentClass = ResolveShellClassFromAnnotation(offset, method.ReturnTypeName);

                if (currentClass is null)
                {
                    return null;
                }
            }

            return currentClass;
        }

        var directClass = ResolveVisibleShellClass(offset, reference);

        if (directClass is not null)
        {
            return directClass;
        }

        var parts = reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return null;
        }

        var rootShellClass = ResolveVisibleShellClass(offset, parts[0]);

        if (rootShellClass is null)
        {
            return null;
        }

        var current = rootShellClass;
        var staticContext = true;

        foreach (var segment in parts.Skip(1))
        {
            var property = current.Properties
                .FirstOrDefault(candidate => !candidate.IsStatic &&
                                             string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));

            if (!staticContext && property is not null && !property.IsHidden)
            {
                current = ResolveShellClassFromAnnotation(offset, property.TypeName);

                if (current is null)
                {
                    return null;
                }

                staticContext = false;
                continue;
            }

            var method = current.Methods
                .Where(candidate => candidate.IsStatic == staticContext &&
                                    !candidate.IsHidden &&
                                    string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Parameters.Count)
                .FirstOrDefault();

            if (method is null)
            {
                return null;
            }

            current = ResolveShellClassFromAnnotation(offset, method.ReturnTypeName);

            if (current is null)
            {
                return null;
            }

            staticContext = false;
        }

        return current;
    }

    public Type? ResolveReferenceType(int offset, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (reference.StartsWith('$'))
        {
            var trimmed = reference[1..];
            var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length == 0)
            {
                return null;
            }

            var rootType = ResolveVisibleVariableType(offset, segments[0]);

            if (rootType is not null)
            {
                return ResolveInstanceMemberChainType(rootType, segments.Skip(1).ToArray());
            }

            var rootShellClass = ResolveVisibleVariableShellClass(offset, segments[0]);
            return rootShellClass is null
                ? null
                : ResolveShellInstanceMemberChainType(offset, rootShellClass, segments.Skip(1).ToArray(), includeHidden: string.Equals(segments[0], "this", StringComparison.Ordinal));
        }

        var resolver = CreateTypeResolver(offset);
        var directType = resolver.Resolve(reference);

        if (directType is not null)
        {
            return directType;
        }

        return ResolveQualifiedStaticAccessType(reference, offset);
    }

    private TypedBinding? ResolveVisibleBinding(int offset, string name)
    {
        return _bindings
            .Where(binding =>
                string.Equals(binding.Name, name, StringComparison.Ordinal) &&
                binding.ScopeStart <= offset &&
                offset <= binding.ScopeEnd &&
                binding.SelectionStart <= offset)
            .OrderByDescending(binding => binding.ScopeDepth)
            .ThenByDescending(binding => binding.SelectionStart)
            .FirstOrDefault();
    }

    private Type? ResolveBindingType(TypedBinding binding)
    {
        if (_bindingTypeCache.TryGetValue(binding, out var cached))
        {
            return cached;
        }

        _bindingTypeCache[binding] = null;

        Type? resolvedType;

        if (!string.IsNullOrWhiteSpace(binding.ExplicitTypeName))
        {
            resolvedType = CreateTypeResolver(binding.SelectionStart).Resolve(NormalizeTypeName(binding.ExplicitTypeName!));
        }
        else if (binding.Source is not null)
        {
            var sourceType = ResolvePipelineType(binding.Source, binding.SelectionStart);
            resolvedType = TryGetEnumerableElementType(sourceType) ?? typeof(object);
        }
        else if (binding.Value is not null)
        {
            resolvedType = ResolvePipelineType(binding.Value, binding.SelectionStart);
        }
        else
        {
            resolvedType = null;
        }

        _bindingTypeCache[binding] = resolvedType;
        return resolvedType;
    }

    private ShellClassSymbol? ResolveBindingShellClass(TypedBinding binding)
    {
        if (_bindingShellClassCache.TryGetValue(binding, out var cached))
        {
            return cached;
        }

        _bindingShellClassCache[binding] = null;

        ShellClassSymbol? resolvedClass;

        if (!string.IsNullOrWhiteSpace(binding.ExplicitTypeName))
        {
            resolvedClass = ResolveVisibleShellClass(binding.SelectionStart, NormalizeTypeName(binding.ExplicitTypeName!));
        }
        else if (binding.Source is not null)
        {
            resolvedClass = ResolvePipelineShellClass(binding.Source, binding.SelectionStart);
        }
        else if (binding.Value is not null)
        {
            resolvedClass = ResolvePipelineShellClass(binding.Value, binding.SelectionStart);
        }
        else
        {
            resolvedClass = null;
        }

        _bindingShellClassCache[binding] = resolvedClass;
        return resolvedClass;
    }

    private Type? ResolvePipelineType(PipelineSyntax pipeline, int offset)
    {
        if (pipeline.Stages.Count != 1 || pipeline.Redirections is { Count: > 0 })
        {
            return null;
        }

        return pipeline.Stages[0] switch
        {
            ExpressionPipelineStageSyntax expression => ResolveArgumentType(expression.Expression, offset),
            _ => null,
        };
    }

    internal ShellClassSymbol? ResolvePipelineShellClass(PipelineSyntax pipeline, int offset)
    {
        if (pipeline.Stages.Count != 1 || pipeline.Redirections is { Count: > 0 })
        {
            return null;
        }

        return pipeline.Stages[0] switch
        {
            ExpressionPipelineStageSyntax expression => ResolveArgumentShellClass(expression.Expression, offset),
            _ => null,
        };
    }

    public Type? ResolveArgumentType(ArgumentSyntax argument, int offset)
    {
        switch (argument)
        {
            case SplatArgumentSyntax splat:
                return ResolveArgumentType(splat.Value, offset);

            case LiteralArgumentSyntax literal:
                return literal.Value?.GetType();

            case InterpolatedStringArgumentSyntax:
            case NameOfArgumentSyntax:
                return typeof(string);

            case VariableReferenceArgumentSyntax variableReference:
                return ResolveVisibleVariableType(offset, variableReference.Name);

            case NewObjectArgumentSyntax newObject:
                return CreateTypeResolver(offset).Resolve(newObject.TypeName);

            case StaticMethodCallArgumentSyntax staticMethodCall:
                return ResolveStaticMethodOrConstructorType(staticMethodCall.Path, staticMethodCall.Arguments.Count, offset);

            case StaticMemberAccessArgumentSyntax staticMemberAccess:
                return ResolveQualifiedStaticAccessType(staticMemberAccess.Path, offset);

            case ArrayLiteralArgumentSyntax list:
                return ResolveArrayType(list, offset);

            case TupleLiteralArgumentSyntax:
                return typeof(ToshTuple);

            case SetLiteralArgumentSyntax:
                return typeof(HashSet<object>);

            case ComparisonPatternSyntax:
                return typeof(bool);

            case RecordLiteralArgumentSyntax:
                return typeof(ExpandoObject);

            case MemberAccessArgumentSyntax memberAccess:
                {
                    var targetType = ResolveArgumentType(memberAccess.Target, offset);
                    return targetType is null ? null : ResolveInstanceMemberChainType(targetType, [memberAccess.MemberPath]);
                }

            case MethodCallArgumentSyntax methodCall:
                {
                    var targetType = ResolveArgumentType(methodCall.Target, offset);
                    return targetType is null ? null : ResolveMethodReturnType(targetType, methodCall.MethodName, methodCall.Arguments.Count, staticOnly: false);
                }

            case SubexpressionArgumentSyntax subexpression:
                return ResolvePipelineType(subexpression.Pipeline, offset);

            case OperatorArgumentSyntax operation:
                return ResolveOperatorType(operation, offset);

            case MatchArgumentSyntax match:
                return ResolveMatchType(match, offset);

            case UnaryOperatorArgumentSyntax:
                return typeof(bool);

            case RangeArgumentSyntax:
                return typeof(ToshRange);

            default:
                return null;
        }
    }

    internal ShellClassSymbol? ResolveArgumentShellClass(ArgumentSyntax argument, int offset)
    {
        switch (argument)
        {
            case SplatArgumentSyntax splat:
                return ResolveArgumentShellClass(splat.Value, offset);

            case VariableReferenceArgumentSyntax variableReference:
                return ResolveVisibleVariableShellClass(offset, variableReference.Name);

            case NewObjectArgumentSyntax newObject:
                return ResolveVisibleShellClass(offset, NormalizeTypeName(newObject.TypeName));

            case StaticMethodCallArgumentSyntax staticMethodCall:
                return ResolveShellStaticCallClass(offset, staticMethodCall.Path);

            case ArrayLiteralArgumentSyntax list:
                {
                    var itemClasses = list.Items
                        .Select(item => ResolveArgumentShellClass(item, offset))
                        .Where(symbol => symbol is not null)
                        .DistinctBy(symbol => symbol!.Name, StringComparer.Ordinal)
                        .ToArray();

                    return itemClasses.Length == 1 ? itemClasses[0] : null;
                }

            case MatchArgumentSyntax match:
                return ResolveMatchShellClass(match, offset);

            case MemberAccessArgumentSyntax memberAccess:
                {
                    var targetClass = ResolveArgumentShellClass(memberAccess.Target, offset);

                    if (targetClass is null)
                    {
                        return null;
                    }

                    var property = targetClass.Properties
                        .FirstOrDefault(candidate => !candidate.IsStatic &&
                                                     string.Equals(candidate.Name, memberAccess.MemberPath, StringComparison.OrdinalIgnoreCase));

                    return property is null
                        ? null
                        : ResolveShellClassFromAnnotation(offset, property.TypeName);
                }

            case MethodCallArgumentSyntax methodCall:
                {
                    var targetClass = ResolveArgumentShellClass(methodCall.Target, offset);

                    if (targetClass is null)
                    {
                        return null;
                    }

                    var method = targetClass.Methods
                        .Where(candidate => !candidate.IsStatic &&
                                            string.Equals(candidate.Name, methodCall.MethodName, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(candidate => Math.Abs(candidate.Parameters.Count - methodCall.Arguments.Count))
                        .FirstOrDefault();

                    return method is null
                        ? null
                        : ResolveShellClassFromAnnotation(offset, method.ReturnTypeName);
                }

            case SubexpressionArgumentSyntax subexpression:
                return ResolvePipelineShellClass(subexpression.Pipeline, offset);

            default:
                return null;
        }
    }

    private Type? ResolveArrayType(ArrayLiteralArgumentSyntax list, int offset)
    {
        var elementTypes = list.Items
            .Select(item => ResolveArgumentType(item, offset))
            .Where(type => type is not null)
            .Distinct()
            .Cast<Type>()
            .ToArray();

        if (elementTypes.Length == 1)
        {
            return elementTypes[0].MakeArrayType();
        }

        return typeof(object[]);
    }

    private Type? ResolveOperatorType(OperatorArgumentSyntax operation, int offset)
    {
        return operation.Operator switch
        {
            "and" or "or" or "==" or "!=" or "=" or ">=" or ">" or "<=" or "<" or "=~" or "!~" or "in" or "not-in" or "contains" or "starts-with" or "ends-with" => typeof(bool),
            "+" or "-" or "*" or "/" or "%" => ResolveArgumentType(operation.Left, offset),
            "??" => ResolveArgumentType(operation.Left, offset) ?? ResolveArgumentType(operation.Right, offset),
            _ => null,
        };
    }

    private Type? ResolveMatchType(MatchArgumentSyntax match, int offset)
    {
        Type? candidate = null;

        foreach (var arm in match.Arms)
        {
            var armType = arm.Body switch
            {
                MatchArmPipelineBodySyntax pipelineBody => ResolvePipelineType(pipelineBody.Pipeline, offset),
                _ => null,
            };

            if (armType is null)
            {
                return null;
            }

            if (candidate is null)
            {
                candidate = armType;
                continue;
            }

            if (candidate != armType)
            {
                return null;
            }
        }

        return candidate;
    }

    private ShellClassSymbol? ResolveMatchShellClass(MatchArgumentSyntax match, int offset)
    {
        ShellClassSymbol? candidate = null;

        foreach (var arm in match.Arms)
        {
            var armClass = arm.Body switch
            {
                MatchArmPipelineBodySyntax pipelineBody => ResolvePipelineShellClass(pipelineBody.Pipeline, offset),
                _ => null,
            };

            if (armClass is null)
            {
                return null;
            }

            if (candidate is null)
            {
                candidate = armClass;
                continue;
            }

            if (!string.Equals(candidate.Name, armClass.Name, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return candidate;
    }

    private Type? ResolveStaticMethodOrConstructorType(string path, int argumentCount, int offset)
    {
        var resolver = CreateTypeResolver(offset);
        var directType = resolver.Resolve(path);

        if (directType is not null)
        {
            return directType;
        }

        var segments = SplitQualifiedPath(path);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = resolver.Resolve(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            return ResolveMethodReturnType(type, segments[^1], argumentCount, staticOnly: true);
        }

        return null;
    }

    private Type? ResolveQualifiedStaticAccessType(string path, int offset)
    {
        var resolver = CreateTypeResolver(offset);
        var segments = SplitQualifiedPath(path);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = resolver.Resolve(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            return ResolveStaticMemberChainType(type, segments[prefixLength..]);
        }

        return null;
    }

    private static Type? ResolveStaticMemberChainType(Type type, IReadOnlyList<string> memberSegments)
    {
        var currentType = type;
        var staticOnly = true;

        foreach (var segment in memberSegments)
        {
            var nextType = ResolveMemberType(currentType, segment, staticOnly);

            if (nextType is null)
            {
                return null;
            }

            currentType = nextType;
            staticOnly = false;
        }

        return currentType;
    }

    private static Type? ResolveInstanceMemberChainType(Type type, IReadOnlyList<string> memberSegments)
    {
        var currentType = type;

        foreach (var segment in memberSegments)
        {
            var nextType = ResolveMemberType(currentType, segment, staticOnly: false);

            if (nextType is null)
            {
                return null;
            }

            currentType = nextType;
        }

        return currentType;
    }

    private Type? ResolveShellInstanceMemberChainType(int offset, ShellClassSymbol shellClass, IReadOnlyList<string> memberSegments, bool includeHidden)
    {
        if (memberSegments.Count == 0)
        {
            return null;
        }

        Type? currentClrType = null;
        ShellClassSymbol? currentShellClass = shellClass;

        foreach (var segment in memberSegments)
        {
            if (currentClrType is not null)
            {
                currentClrType = ResolveMemberType(currentClrType, segment, staticOnly: false);

                if (currentClrType is null)
                {
                    return null;
                }

                continue;
            }

            if (currentShellClass is null)
            {
                return null;
            }

            var property = currentShellClass.Properties
                .FirstOrDefault(candidate => !candidate.IsStatic &&
                                             (includeHidden || !candidate.IsHidden) &&
                                             string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));

            if (property is null)
            {
                return null;
            }

            var typeName = NormalizeTypeName(property.TypeName);
            currentShellClass = ResolveShellClassFromAnnotation(offset, typeName);

            if (currentShellClass is not null)
            {
                continue;
            }

            currentClrType = ResolveClrTypeFromAnnotation(offset, typeName);

            if (currentClrType is null)
            {
                return null;
            }
        }

        return currentClrType;
    }

    private ShellReferenceSymbol? ResolveShellReferenceCore(
        int offset,
        ShellClassSymbol shellClass,
        IReadOnlyList<string> segments,
        bool isStaticContext,
        bool includeHidden)
    {
        var currentClass = shellClass;
        var staticContext = isStaticContext;

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var isLast = index == segments.Count - 1;

            var property = currentClass.Properties
                .FirstOrDefault(candidate => candidate.IsStatic == staticContext &&
                                             (includeHidden || !candidate.IsHidden) &&
                                             string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase));

            if (property is not null)
            {
                if (isLast)
                {
                    return new ShellReferenceSymbol.Property(currentClass, property);
                }

                var nextClass = ResolveShellClassFromAnnotation(offset, property.TypeName);

                if (nextClass is null)
                {
                    return null;
                }

                currentClass = nextClass;
                staticContext = false;
                continue;
            }

            var methods = currentClass.Methods
                .Where(candidate => candidate.IsStatic == staticContext &&
                                    (includeHidden || !candidate.IsHidden) &&
                                    string.Equals(candidate.Name, segment, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Parameters.Count)
                .ToArray();

            if (methods.Length == 0)
            {
                return null;
            }

            if (isLast)
            {
                return new ShellReferenceSymbol.Method(currentClass, methods);
            }

            var nextMethodClass = ResolveShellClassFromAnnotation(offset, methods[0].ReturnTypeName);

            if (nextMethodClass is null)
            {
                return null;
            }

            currentClass = nextMethodClass;
            staticContext = false;
        }

        return new ShellReferenceSymbol.Class(currentClass);
    }

    private ShellClassSymbol? ResolveShellStaticCallClass(int offset, string path)
    {
        var directClass = ResolveVisibleShellClass(offset, path);

        if (directClass is not null)
        {
            return directClass;
        }

        var segments = SplitQualifiedPath(path);

        if (segments.Length == 2 &&
            ResolveVisibleShellClass(offset, segments[0]) is { } shellClass)
        {
            var method = shellClass.Methods
                .Where(candidate => candidate.IsStatic &&
                                    string.Equals(candidate.Name, segments[1], StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Parameters.Count)
                .FirstOrDefault();

            return method is null
                ? null
                : ResolveShellClassFromAnnotation(offset, method.ReturnTypeName);
        }

        return null;
    }

    private ShellClassSymbol? ResolveShellClassFromAnnotation(int offset, string? typeName)
    {
        var normalized = NormalizeTypeName(typeName);
        return string.IsNullOrWhiteSpace(normalized) ? null : ResolveVisibleShellClass(offset, normalized);
    }

    private Type? ResolveClrTypeFromAnnotation(int offset, string? typeName)
    {
        var normalized = NormalizeTypeName(typeName);
        return string.IsNullOrWhiteSpace(normalized) ? null : CreateTypeResolver(offset).Resolve(normalized);
    }

    private static string NormalizeTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return string.Empty;
        }

        var trimmed = typeName.Trim();
        return trimmed.EndsWith("?", StringComparison.Ordinal) ? trimmed[..^1] : trimmed;
    }

    private static Type? ResolveMethodReturnType(Type type, string methodName, int argumentCount, bool staticOnly)
    {
        var bindingFlags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);
        var method = type.GetMethods(bindingFlags)
            .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
            .OrderBy(candidate => Math.Abs(candidate.GetParameters().Length - argumentCount))
            .FirstOrDefault();

        if (method is null)
        {
            return null;
        }

        return method.ReturnType == typeof(void)
            ? type
            : method.ReturnType;
    }

    private static Type? ResolveMemberType(Type type, string memberName, bool staticOnly)
    {
        if (staticOnly)
        {
            var nestedType = type.GetNestedType(memberName, BindingFlags.Public);

            if (nestedType is not null)
            {
                return nestedType;
            }
        }

        var bindingFlags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);
        var property = type.GetProperty(memberName, bindingFlags);

        if (property is not null)
        {
            return property.PropertyType;
        }

        var field = type.GetField(memberName, bindingFlags);

        if (field is not null)
        {
            return field.FieldType;
        }

        return null;
    }

    private static Type? TryGetEnumerableElementType(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        var enumerableInterface = type
            .GetInterfaces()
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerableInterface?.GetGenericArguments()[0];
    }

    private static string[] SplitQualifiedPath(string path)
    {
        return path
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private void LoadVisibleRequiredAssemblies(int offset)
    {
        var documentPath = TryGetDocumentPath(_sourceName);

        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return;
        }

        foreach (var directive in _requireDirectives.Where(directive => directive.SelectionStart <= offset))
        {
            foreach (var assemblyPath in ResolveRequiredAssemblyPaths(documentPath!, directive.Target))
            {
                LoadAssemblyIfPossible(assemblyPath);
            }
        }
    }

    private static IEnumerable<string> ResolveRequiredAssemblyPaths(string documentPath, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            yield break;
        }

        var baseDirectory = Path.GetDirectoryName(documentPath);

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            yield break;
        }

        var candidate = Path.IsPathRooted(target)
            ? target
            : Path.GetFullPath(Path.Combine(baseDirectory, target));

        if (File.Exists(candidate))
        {
            var extension = Path.GetExtension(candidate);

            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                yield return candidate;
                yield break;
            }

            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) &&
                TryResolveProjectAssembly(candidate, out var projectAssembly))
            {
                yield return projectAssembly;
            }

            yield break;
        }

        if (!Path.HasExtension(candidate))
        {
            var dllCandidate = candidate + ".dll";

            if (File.Exists(dllCandidate))
            {
                yield return dllCandidate;
            }
        }
    }

    private static bool TryResolveProjectAssembly(string projectPath, out string assemblyPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath);
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(projectName))
        {
            assemblyPath = string.Empty;
            return false;
        }

        var binDirectory = Path.Combine(projectDirectory, "bin");

        if (!Directory.Exists(binDirectory))
        {
            assemblyPath = string.Empty;
            return false;
        }

        assemblyPath = Directory
            .EnumerateFiles(binDirectory, projectName + ".dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() ?? string.Empty;

        return !string.IsNullOrWhiteSpace(assemblyPath);
    }

    private static void LoadAssemblyIfPossible(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            return;
        }

        var normalizedPath = Path.GetFullPath(assemblyPath);

        lock (AssemblyLoadSync)
        {
            if (!AttemptedAssemblyLoads.Add(normalizedPath))
            {
                return;
            }
        }

        try
        {
            var assemblyName = AssemblyName.GetAssemblyName(normalizedPath);
            var existingAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));

            if (existingAssembly is not null)
            {
                return;
            }

            AssemblyLoadContext.Default.LoadFromAssemblyPath(normalizedPath);
        }
        catch
        {
        }
    }

    private static string? TryGetDocumentPath(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return null;
        }

        if (Uri.TryCreate(sourceName, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        return Path.IsPathRooted(sourceName) ? sourceName : null;
    }

    private sealed record UsingDirective(string Target, string? Alias, int SelectionStart);

    private sealed record RequireDirective(string Target, int SelectionStart);

    private sealed record TypedBinding(
        string Name,
        string? ExplicitTypeName,
        PipelineSyntax? Value,
        PipelineSyntax? Source,
        int ScopeStart,
        int ScopeEnd,
        int ScopeDepth,
        int SelectionStart);

    public sealed record ShellClassSymbol(
        string Name,
        IReadOnlyList<ShellClassPropertySymbol> Properties,
        IReadOnlyList<ShellClassMethodSymbol> Methods,
        IReadOnlyList<ShellClassConstructorSymbol> Constructors);

    public sealed record ShellClassPropertySymbol(
        string Name,
        string? TypeName,
        bool IsStatic,
        bool IsWritable,
        bool IsComputed,
        bool IsHidden,
        string? DocDescription = null);

    public sealed record ShellClassMethodSymbol(
        string Name,
        string? ReturnTypeName,
        IReadOnlyList<FunctionParameterSyntax> Parameters,
        bool IsStatic,
        bool IsHidden,
        string? DocDescription = null);

    public sealed record ShellClassConstructorSymbol(
        IReadOnlyList<FunctionParameterSyntax> Parameters);

    public abstract record ShellReferenceSymbol
    {
        private ShellReferenceSymbol() { }

        public sealed record Class(ShellClassSymbol Symbol) : ShellReferenceSymbol;

        public sealed record Property(ShellClassSymbol DeclaringClass, ShellClassPropertySymbol Symbol) : ShellReferenceSymbol;

        public sealed record Method(ShellClassSymbol DeclaringClass, IReadOnlyList<ShellClassMethodSymbol> Overloads) : ShellReferenceSymbol;
    }

    private sealed record ShellClassDeclaration(
        ShellClassSymbol Symbol,
        int ScopeStart,
        int ScopeEnd,
        int ScopeDepth,
        int SelectionStart);

    private sealed class Collector
    {
        private readonly List<UsingDirective> _usingDirectives = new();
        private readonly List<RequireDirective> _requireDirectives = new();
        private readonly List<TypedBinding> _bindings = new();
        private readonly List<ShellClassDeclaration> _classDeclarations = new();

        public IReadOnlyList<UsingDirective> UsingDirectives => _usingDirectives;

        public IReadOnlyList<RequireDirective> RequireDirectives => _requireDirectives;

        public IReadOnlyList<TypedBinding> Bindings => _bindings;

        public IReadOnlyList<ShellClassDeclaration> ClassDeclarations => _classDeclarations;

        public void Collect(StatementSyntax statement, TextSpan scopeSpan, int depth)
        {
            switch (statement)
            {
                case ScriptStatementSyntax script:
                    foreach (var child in script.Statements)
                    {
                        Collect(child, scopeSpan, depth);
                    }
                    break;

                case UsingStatementSyntax @using:
                    _usingDirectives.Add(new UsingDirective(@using.Target, @using.Alias, @using.Span.Start));
                    break;

                case RequireStatementSyntax require:
                    _requireDirectives.Add(new RequireDirective(require.Target, require.Span.Start));
                    break;

                case VariableDeclarationStatementSyntax variable:
                    _bindings.Add(new TypedBinding(
                        variable.Name,
                        variable.TypeName,
                        variable.Value,
                        null,
                        scopeSpan.Start,
                        scopeSpan.End,
                        depth,
                        variable.Span.Start));
                    if (variable.Value is not null)
                    {
                        CollectPipeline(variable.Value, scopeSpan, depth);
                    }
                    break;

                case FunctionDefinitionStatementSyntax function:
                    AddParameterBindings(function.Parameters, function.Body.Span, depth + 1);

                    CollectBlock(function.Body, depth + 1);
                    break;

                case ClassDefinitionStatementSyntax @class:
                    var classSymbol = CreateShellClassSymbol(@class);
                    _classDeclarations.Add(new ShellClassDeclaration(
                        classSymbol,
                        scopeSpan.Start,
                        scopeSpan.End,
                        depth,
                        @class.Span.Start));
                    CollectClassMembers(@class, classSymbol, depth + 1);
                    break;

                case ForStatementSyntax @for:
                    AddBinding(
                        @for.VariableName,
                        typeName: null,
                        value: null,
                        source: @for.Source,
                        scopeSpan: @for.Body.Span,
                        depth: depth + 1,
                        selectionStart: @for.Span.Start);
                    CollectPipeline(@for.Source, scopeSpan, depth);
                    CollectBlock(@for.Body, depth + 1);
                    break;

                case IfStatementSyntax @if:
                    CollectArgument(@if.Condition, scopeSpan, depth);
                    CollectBlock(@if.ThenBlock, depth + 1);
                    if (@if.ElseBlock is not null)
                    {
                        CollectBlock(@if.ElseBlock, depth + 1);
                    }
                    break;

                case WhileStatementSyntax @while:
                    CollectArgument(@while.Condition, scopeSpan, depth);
                    CollectBlock(@while.Body, depth + 1);
                    break;

                case UntilStatementSyntax until:
                    CollectArgument(until.Condition, scopeSpan, depth);
                    CollectBlock(until.Body, depth + 1);
                    break;

                case TryStatementSyntax @try:
                    CollectBlock(@try.TryBlock, depth + 1);
                    if (@try.CatchClause is not null)
                    {
                        CollectBlock(@try.CatchClause.Body, depth + 1);
                    }
                    if (@try.FinallyBlock is not null)
                    {
                        CollectBlock(@try.FinallyBlock, depth + 1);
                    }
                    break;

                case DeferStatementSyntax @defer:
                    CollectBlock(@defer.Body, depth + 1);
                    break;

                case SwitchStatementSyntax @switch:
                    CollectArgument(@switch.Value, scopeSpan, depth);
                    foreach (var @case in @switch.Cases)
                    {
                        CollectArgument(@case.MatchExpression, scopeSpan, depth);
                        CollectBlock(@case.Body, depth + 1);
                    }
                    if (@switch.DefaultBlock is not null)
                    {
                        CollectBlock(@switch.DefaultBlock, depth + 1);
                    }
                    break;

                case PipelineStatementSyntax pipelineStatement:
                    CollectPipeline(pipelineStatement.Pipeline, scopeSpan, depth);
                    break;

                case ReturnStatementSyntax @return when @return.Value is not null:
                    CollectPipeline(@return.Value, scopeSpan, depth);
                    break;

                case ThrowStatementSyntax @throw when @throw.Value is not null:
                    CollectPipeline(@throw.Value, scopeSpan, depth);
                    break;

                case VariableAssignmentStatementSyntax assignment:
                    CollectPipeline(assignment.Value, scopeSpan, depth);
                    break;

                case MemberAssignmentStatementSyntax assignment:
                    CollectArgument(assignment.Target, scopeSpan, depth);
                    CollectPipeline(assignment.Value, scopeSpan, depth);
                    break;
            }
        }

        private void CollectClassMembers(ClassDefinitionStatementSyntax @class, ShellClassSymbol classSymbol, int depth)
        {
            foreach (var member in @class.Members)
            {
                switch (member)
                {
                    case ClassPropertyMemberSyntax property:
                        if (property.Initializer is not null)
                        {
                            CollectPipeline(property.Initializer, @class.Span, depth);
                        }

                        if (property.GetterBody is not null)
                        {
                            AddBinding("this", classSymbol.Name, null, null, property.GetterBody.Span, depth, property.Span.Start);
                            CollectBlock(property.GetterBody, depth);
                        }

                        if (property.SetterBody is not null)
                        {
                            AddBinding("this", classSymbol.Name, null, null, property.SetterBody.Span, depth, property.Span.Start);
                            AddBinding("value", property.TypeName, null, null, property.SetterBody.Span, depth, property.Span.Start);
                            CollectBlock(property.SetterBody, depth);
                        }

                        break;

                    case ClassConstructorMemberSyntax constructor:
                        AddBinding("this", classSymbol.Name, null, null, constructor.Body.Span, depth, constructor.Span.Start);
                        AddParameterBindings(constructor.Parameters, constructor.Body.Span, depth);
                        CollectBlock(constructor.Body, depth);
                        break;

                    case ClassMethodMemberSyntax methodMember:
                        if (!methodMember.IsStatic)
                        {
                            AddBinding("this", classSymbol.Name, null, null, methodMember.Method.Body.Span, depth, methodMember.Span.Start);
                        }

                        AddParameterBindings(methodMember.Method.Parameters, methodMember.Method.Body.Span, depth);
                        CollectBlock(methodMember.Method.Body, depth);
                        break;
                }
            }
        }

        private void AddParameterBindings(IReadOnlyList<FunctionParameterSyntax> parameters, TextSpan scopeSpan, int depth)
        {
            foreach (var parameter in parameters)
            {
                AddBinding(parameter.Name, parameter.TypeName, null, null, scopeSpan, depth, parameter.Span.Start);
            }
        }

        private void AddBinding(
            string name,
            string? typeName,
            PipelineSyntax? value,
            PipelineSyntax? source,
            TextSpan scopeSpan,
            int depth,
            int selectionStart)
        {
            _bindings.Add(new TypedBinding(
                name,
                typeName,
                value,
                source,
                scopeSpan.Start,
                scopeSpan.End,
                depth,
                selectionStart));
        }

        private static ShellClassSymbol CreateShellClassSymbol(ClassDefinitionStatementSyntax @class)
        {
            var properties = @class.Members
                .OfType<ClassPropertyMemberSyntax>()
                .Select(property => new ShellClassPropertySymbol(
                    property.Name,
                    property.TypeName,
                    IsStatic: false,
                    IsWritable: property.SetterBody is not null || property.GetterBody is null,
                    IsComputed: property.GetterBody is not null,
                    IsHidden: property.IsShy,
                    DocDescription: property.DocComment?.Description is { Length: > 0 } propDesc ? propDesc : null))
                .ToArray();

            var methods = @class.Members
                .OfType<ClassMethodMemberSyntax>()
                .Select(methodMember => new ShellClassMethodSymbol(
                    methodMember.Method.Name,
                    methodMember.Method.ReturnTypeName,
                    methodMember.Method.Parameters,
                    methodMember.IsStatic,
                    methodMember.IsShy,
                    DocDescription: methodMember.Method.DocComment?.Description is { Length: > 0 } methDesc ? methDesc : null))
                .ToArray();

            var constructors = @class.Members
                .OfType<ClassConstructorMemberSyntax>()
                .Select(constructor => new ShellClassConstructorSymbol(constructor.Parameters))
                .ToList();

            if (@class.PrimaryConstructorParameters.Count > 0)
            {
                constructors.Add(new ShellClassConstructorSymbol(@class.PrimaryConstructorParameters));
            }

            if (constructors.Count == 0)
            {
                constructors.Add(new ShellClassConstructorSymbol(Array.Empty<FunctionParameterSyntax>()));
            }

            return new ShellClassSymbol(@class.Name, properties, methods, constructors);
        }

        private void CollectBlock(BlockSyntax block, int depth)
        {
            foreach (var statement in block.Statements)
            {
                Collect(statement, block.Span, depth);
            }
        }

        private void CollectPipeline(PipelineSyntax pipeline, TextSpan scopeSpan, int depth)
        {
            foreach (var stage in pipeline.Stages)
            {
                switch (stage)
                {
                    case CommandSyntax command:
                        foreach (var argument in command.Arguments)
                        {
                            CollectArgument(argument, scopeSpan, depth);
                        }
                        break;

                    case ExpressionPipelineStageSyntax expression:
                        CollectArgument(expression.Expression, scopeSpan, depth);
                        break;
                }
            }

            if (pipeline.Redirections is null)
            {
                return;
            }

            foreach (var redirection in pipeline.Redirections)
            {
                CollectArgument(redirection.Target, scopeSpan, depth);
            }
        }

        private void CollectArgument(ArgumentSyntax argument, TextSpan scopeSpan, int depth)
        {
            switch (argument)
            {
                case SplatArgumentSyntax splat:
                    CollectArgument(splat.Value, scopeSpan, depth);
                    break;

                case NewObjectArgumentSyntax newObject:
                    foreach (var child in newObject.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case StaticMethodCallArgumentSyntax staticCall:
                    foreach (var child in staticCall.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case ArrayLiteralArgumentSyntax list:
                    foreach (var item in list.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case TupleLiteralArgumentSyntax tuple:
                    foreach (var item in tuple.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case SetLiteralArgumentSyntax set:
                    foreach (var item in set.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case ComparisonPatternSyntax comparisonPattern:
                    CollectArgument(comparisonPattern.Operand, scopeSpan, depth);
                    break;

                case RecordLiteralArgumentSyntax record:
                    foreach (var entry in record.Fields)
                    {
                        if (entry is RecordFieldSyntax field)
                        {
                            CollectArgument(field.Value, scopeSpan, depth);
                        }
                        else if (entry is ComputedRecordFieldSyntax computed)
                        {
                            CollectArgument(computed.NameExpression, scopeSpan, depth);
                            CollectArgument(computed.Value, scopeSpan, depth);
                        }
                        else if (entry is SpreadRecordEntrySyntax spread)
                        {
                            CollectArgument(spread.Value, scopeSpan, depth);
                        }
                    }
                    break;

                case BlockArgumentSyntax blockArgument:
                    CollectBlock(blockArgument.Block, depth + 1);
                    break;

                case MemberAccessArgumentSyntax member:
                    CollectArgument(member.Target, scopeSpan, depth);
                    break;

                case MethodCallArgumentSyntax method:
                    CollectArgument(method.Target, scopeSpan, depth);
                    foreach (var child in method.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case SubexpressionArgumentSyntax subexpression:
                    CollectPipeline(subexpression.Pipeline, scopeSpan, depth);
                    break;

                case OperatorArgumentSyntax operation:
                    CollectArgument(operation.Left, scopeSpan, depth);
                    CollectArgument(operation.Right, scopeSpan, depth);
                    break;

                case UnaryOperatorArgumentSyntax unary:
                    CollectArgument(unary.Operand, scopeSpan, depth);
                    break;

                case RangeArgumentSyntax range:
                    CollectArgument(range.Start, scopeSpan, depth);
                    if (range.Step is not null)
                    {
                        CollectArgument(range.Step, scopeSpan, depth);
                    }
                    if (range.End is not null)
                    {
                        CollectArgument(range.End, scopeSpan, depth);
                    }
                    break;

                case MatchArgumentSyntax match:
                    CollectArgument(match.Value, scopeSpan, depth);
                    foreach (var arm in match.Arms)
                    {
                        if (arm.Pattern is not null)
                        {
                            CollectArgument(arm.Pattern, scopeSpan, depth);
                        }

                        if (arm.Guard is not null)
                        {
                            CollectArgument(arm.Guard, scopeSpan, depth);
                        }

                        switch (arm.Body)
                        {
                            case MatchArmPipelineBodySyntax pipelineBody:
                                CollectPipeline(pipelineBody.Pipeline, scopeSpan, depth);
                                break;
                            case MatchArmBlockBodySyntax blockBody:
                                CollectBlock(blockBody.Block, depth + 1);
                                break;
                        }
                    }
                    break;
            }
        }

    }
}
