using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language;

public sealed class ToshClassDefinition : IShellNamedType
{
    private readonly ToshEngine _engine;
    private readonly Dictionary<string, ToshClassPropertyDefinition> _propertiesByName;
    private readonly Dictionary<string, IReadOnlyList<ToshClassMethodDefinition>> _methodsByName;
    private readonly IReadOnlyList<ToshClassConstructorDefinition> _constructors;
    private readonly IReadOnlyList<FunctionParameterDefinition> _primaryConstructorParameters;

    public ToshClassDefinition(
        ToshEngine engine,
        string name,
        IReadOnlyList<FunctionParameterDefinition> primaryConstructorParameters,
        IReadOnlyList<ToshClassPropertyDefinition> properties,
        IReadOnlyList<ToshClassMethodDefinition> methods,
        IReadOnlyList<ToshClassConstructorDefinition> constructors,
        string sourceName,
        string sourceText,
        TextSpan span,
        IReadOnlyList<LexicalScope>? capturedScopes)
    {
        _engine = engine;
        Name = name;
        _primaryConstructorParameters = primaryConstructorParameters;
        Properties = properties;
        Methods = methods;
        _constructors = constructors;
        SourceName = sourceName;
        SourceText = sourceText;
        Span = span;
        CapturedScopes = capturedScopes;
        _propertiesByName = properties.ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
        _methodsByName = methods
            .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ToshClassMethodDefinition>)group.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public IReadOnlyList<ToshClassPropertyDefinition> Properties { get; }

    public IReadOnlyList<ToshClassMethodDefinition> Methods { get; }

    public string SourceName { get; }

    public string SourceText { get; }

    public TextSpan Span { get; }

    public IReadOnlyList<LexicalScope>? CapturedScopes { get; }

    public string ShellTypeName => Name;

    public string ShellFullName => Name;

    public string? ShellNamespace => null;

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => typeof(object).FullName;

    public bool ShellIsClass => true;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => false;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        var constructor = SelectConstructor(arguments, out var locals);
        var instance = new ToshClassInstance(this);
        instance.Initialize(locals, constructor);
        return instance;
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            throw new InvalidOperationException($"Static method '{methodName}' was not found on class '{Name}'.");
        }

        var staticCandidates = candidates.Where(candidate => candidate.IsStatic && !candidate.IsShy).ToArray();

        if (staticCandidates.Length == 0)
        {
            throw new InvalidOperationException($"'{methodName}' is an instance method on class '{Name}' and cannot be called statically. Create an instance first: var obj = new {Name}(); $obj.{methodName}(...)");
        }

        var method = SelectMethod(staticCandidates, arguments, out var locals);
        var values = ExecuteMethodBlock(method, locals, instance: null);
        return new InvocationResult(FlattenCallResult(values), ReturnedVoid: false);
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = null;

        if (_methodsByName.TryGetValue(memberName, out var candidates))
        {
            var isStatic = candidates.Any(c => c.IsStatic);
            var hint = isStatic
                ? $"'{memberName}' is a method on class '{Name}'. Call it with parentheses: {Name}.{memberName}(...)"
                : $"'{memberName}' is an instance method on class '{Name}'. Create an instance first: var obj = new {Name}(); $obj.{memberName}(...)";
            throw new InvalidOperationException(hint);
        }

        return false;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        foreach (var field in GetMembers(includeHidden))
        {
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        return
        [
            new KeyValuePair<string, object?>("Name", ShellTypeName),
            new KeyValuePair<string, object?>("FullName", ShellFullName),
            new KeyValuePair<string, object?>("Namespace", ShellNamespace),
            new KeyValuePair<string, object?>("Assembly", ShellAssemblyName),
            new KeyValuePair<string, object?>("BaseType", ShellBaseTypeName),
            new KeyValuePair<string, object?>("IsClass", ShellIsClass),
            new KeyValuePair<string, object?>("IsInterface", ShellIsInterface),
            new KeyValuePair<string, object?>("IsEnum", ShellIsEnum),
            new KeyValuePair<string, object?>("IsValueType", ShellIsValueType),
            new KeyValuePair<string, object?>("IsAbstract", ShellIsAbstract),
            new KeyValuePair<string, object?>("IsGenericType", ShellIsGenericType),
            new KeyValuePair<string, object?>("IsArray", ShellIsArray),
            new KeyValuePair<string, object?>("IsPublic", ShellIsPublic),
            new KeyValuePair<string, object?>("PropertyCount", GetShellMembers(includeHidden).Count(member => !member.IsStatic)),
            new KeyValuePair<string, object?>("MethodCount", GetShellMethods(includeHidden).Count(method => !method.IsStatic)),
            new KeyValuePair<string, object?>("StaticMethodCount", GetShellMethods(includeHidden).Count(method => method.IsStatic)),
            new KeyValuePair<string, object?>("ConstructorCount", GetShellConstructors().Count),
        ];
    }

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false)
    {
        return Properties
            .Where(property => includeHidden || !property.IsShy)
            .Select(property => new ShellMemberDescriptor(
                property.Name,
                Kind: "Property",
                TypeName: GetAnnotationDisplayName(property.TypeName),
                IsStatic: false,
                IsWritable: property.IsWritable,
                IsHidden: property.IsShy))
            .ToArray();
    }

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false)
    {
        return Methods
            .Where(method => includeHidden || !method.IsShy)
            .OrderBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .Select(method => new ShellMethodDescriptor(
                method.Name,
                ReturnTypeName: GetAnnotationDisplayName(method.ReturnTypeName),
                IsStatic: method.IsStatic,
                ParameterCount: method.Parameters.Count,
                Signature: FormatMethodSignature(method),
                IsHidden: method.IsShy))
            .ToArray();
    }

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors()
    {
        return GetConstructorMetadata()
            .DistinctBy(constructor => constructor.Signature, StringComparer.Ordinal)
            .ToArray();
    }

    internal bool TryGetInstanceMember(ToshClassInstance instance, string name, bool includeHidden, out object? value)
    {
        if (!_propertiesByName.TryGetValue(name, out var property) ||
            (property.IsShy && !includeHidden))
        {
            value = null;
            return false;
        }

        if (property.GetterBody is not null)
        {
            value = EvaluatePropertyGetter(instance, property);
            return true;
        }

        return instance.TryGetStoredValue(property.Name, out value);
    }

    internal bool TrySetInstanceMember(ToshClassInstance instance, string name, object? value, bool includeHidden)
    {
        if (!_propertiesByName.TryGetValue(name, out var property) ||
            (property.IsShy && !includeHidden))
        {
            return false;
        }

        if (property.SetterBody is not null)
        {
            ExecutePropertySetter(instance, property, value);
            return true;
        }

        if (property.GetterBody is not null)
        {
            throw new InvalidOperationException($"Property '{property.Name}' on class '{Name}' is read-only.");
        }

        instance.SetStoredValue(property.Name, ConvertPropertyValue(property, value));
        return true;
    }

    internal IReadOnlyList<KeyValuePair<string, object?>> GetInstanceMembers(ToshClassInstance instance, bool includeHidden)
    {
        var members = new List<KeyValuePair<string, object?>>();

        foreach (var property in Properties)
        {
            if (property.IsShy && !includeHidden)
            {
                continue;
            }

            TryGetInstanceMember(instance, property.Name, includeHidden, out var value);
            members.Add(new KeyValuePair<string, object?>(property.Name, value));
        }

        return members;
    }

    internal InvocationResult InvokeInstanceMethod(ToshClassInstance instance, string methodName, IReadOnlyList<object?> arguments, bool includeHidden)
    {
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            throw new InvalidOperationException($"Method '{methodName}' was not found on class '{Name}'.");
        }

        var method = SelectMethod(
            candidates.Where(candidate => !candidate.IsStatic && (includeHidden || !candidate.IsShy)).ToArray(),
            arguments,
            out var locals);
        var values = ExecuteMethodBlock(method, locals, instance);
        return new InvocationResult(FlattenCallResult(values), ReturnedVoid: false);
    }

    internal IEnumerable<object?> EnumerateItems(ToshClassInstance instance)
    {
        if (TryInvokeEnumerator(instance, out var value))
        {
            if (value is null)
            {
                yield break;
            }

            foreach (var item in ShellIterationUtilities.ExpandCollectionLikeValue(value))
            {
                yield return item;
            }

            yield break;
        }

        yield return instance;
    }

    internal bool HasSpecialInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        return TrySelectSpecialInstanceMethod(methodName, arguments, out _, out _);
    }

    internal bool TryInvokeSpecialInstanceMethod(ToshClassInstance instance, string methodName, IReadOnlyList<object?> arguments, out object? value)
    {
        value = null;

        if (!TrySelectSpecialInstanceMethod(methodName, arguments, out var method, out var locals))
        {
            return false;
        }

        var values = ExecuteMethodBlock(method, locals, instance);
        value = FlattenCallResult(values);
        return true;
    }

    internal object? GetInitialPropertyValue(ToshClassInstance instance, ToshClassPropertyDefinition property, IReadOnlyDictionary<string, object?> constructorLocals)
    {
        if (property.Initializer is null)
        {
            return null;
        }

        var locals = CreateLocals(instance, constructorLocals);
        var value = _engine.EvaluateClassPipelineValueSync(SourceName, SourceText, property.Initializer, locals, CapturedScopes);
        return ConvertPropertyValue(property, value);
    }

    internal void RunConstructor(ToshClassInstance instance, ToshClassConstructorDefinition constructor, IReadOnlyDictionary<string, object?> constructorLocals)
    {
        var locals = CreateLocals(instance, constructorLocals);
        _engine.ExecuteClassBlockSync(constructor.SourceName, constructor.SourceText, constructor.Body, locals, constructor.CapturedScopes, $"{Name}()");
    }

    private object? EvaluatePropertyGetter(ToshClassInstance instance, ToshClassPropertyDefinition property)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal));
        var values = _engine.ExecuteClassBlockSync(SourceName, SourceText, property.GetterBody!, locals, CapturedScopes, $"{Name}.{property.Name}.get");
        return FlattenCallResult(values);
    }

    private void ExecutePropertySetter(ToshClassInstance instance, ToshClassPropertyDefinition property, object? value)
    {
        var locals = CreateLocals(instance, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = value,
        });
        _engine.ExecuteClassBlockSync(SourceName, SourceText, property.SetterBody!, locals, CapturedScopes, $"{Name}.{property.Name}.set");
    }

    private object? ConvertPropertyValue(ToshClassPropertyDefinition property, object? value)
    {
        if (property.TypeName is null)
        {
            return value;
        }

        if (_engine.TryConvertAnnotatedValue(property.TypeName, value, out var converted))
        {
            return converted;
        }

        throw new InvalidOperationException(
            $"Property '{property.Name}' on class '{Name}' could not be converted to '{property.TypeName}'.");
    }

    private IReadOnlyList<object?> ExecuteMethodBlock(ToshClassMethodDefinition method, IReadOnlyDictionary<string, object?> boundLocals, ToshClassInstance? instance)
    {
        var locals = CreateLocals(instance, boundLocals);
        var values = _engine.ExecuteClassBlockSync(method.SourceName, method.SourceText, method.Body, locals, method.CapturedScopes, $"{Name}.{method.Name}");
        return method.ReturnTypeName is null
            ? UnwrapValues(values)
            : UnwrapValues(values)
                .Select(value => _engine.ConvertAnnotatedValue(method.ReturnTypeName, value, method.Span, method.SourceName, method.SourceText, $"{Name}.{method.Name}"))
                .ToArray();
    }

    private IReadOnlyDictionary<string, object?> CreateLocals(ToshClassInstance? instance, IReadOnlyDictionary<string, object?> locals)
    {
        var result = new Dictionary<string, object?>(locals, StringComparer.Ordinal)
        {
            ["args"] = locals.Values.ToArray(),
        };

        if (instance is not null)
        {
            result["this"] = new ToshClassSelfReference(instance);
        }

        return result;
    }

    private ToshClassConstructorDefinition? SelectConstructor(IReadOnlyList<object?> arguments, out Dictionary<string, object?> locals)
    {
        var matches = _engine.SelectBestCallableMatches(GetConstructorDefinitions(), static candidate => candidate.Parameters, arguments);

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No constructor matched class '{Name}' with {arguments.Count} argument(s).");
        }

        if (matches.Count > 1)
        {
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatConstructorSignature(match.Candidate.Parameters)));
            throw new InvalidOperationException(
                $"Multiple constructor overloads matched class '{Name}' with {arguments.Count} argument(s): {signatures}.");
        }

        locals = matches[0].Locals;
        return matches[0].Candidate;
    }

    private ToshClassMethodDefinition SelectMethod(IReadOnlyList<ToshClassMethodDefinition> candidates, IReadOnlyList<object?> arguments, out Dictionary<string, object?> locals)
    {
        var matches = _engine.SelectBestCallableMatches(candidates, static candidate => candidate.Parameters, arguments);

        if (matches.Count == 0)
        {
            var methodDisplayName = candidates.Count > 0 ? $"'{Name}.{candidates[0].Name}'" : $"'{Name}'";
            throw new InvalidOperationException($"No overload matched {methodDisplayName} with {arguments.Count} argument(s).");
        }

        if (matches.Count > 1)
        {
            var methodDisplayName = candidates.Count > 0 ? $"{Name}.{candidates[0].Name}" : Name;
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatMethodSignature(match.Candidate)));
            throw new InvalidOperationException(
                $"Multiple overloads matched method '{methodDisplayName}' with {arguments.Count} argument(s): {signatures}.");
        }

        locals = matches[0].Locals;
        return matches[0].Candidate;
    }

    private bool TryInvokeEnumerator(ToshClassInstance instance, out object? value)
    {
        foreach (var methodName in new[] { "enumerate", "GetEnumerator" })
        {
            if (!TryInvokeSpecialInstanceMethod(instance, methodName, Array.Empty<object?>(), out value))
            {
                continue;
            }

            return true;
        }

        value = null;
        return false;
    }

    private bool TrySelectSpecialInstanceMethod(
        string methodName,
        IReadOnlyList<object?> arguments,
        out ToshClassMethodDefinition method,
        out Dictionary<string, object?> locals)
    {
        if (!_methodsByName.TryGetValue(methodName, out var candidates))
        {
            method = null!;
            locals = null!;
            return false;
        }

        var matches = _engine.SelectBestCallableMatches(
            candidates.Where(candidate => !candidate.IsStatic),
            static candidate => candidate.Parameters,
            arguments);

        if (matches.Count == 0)
        {
            method = null!;
            locals = null!;
            return false;
        }

        if (matches.Count > 1)
        {
            var signatures = string.Join(
                "; ",
                matches.Select(match => FormatMethodSignature(match.Candidate)));
            throw new InvalidOperationException(
                $"Multiple overloads matched special method '{Name}.{methodName}' with {arguments.Count} argument(s): {signatures}.");
        }

        method = matches[0].Candidate;
        locals = matches[0].Locals;
        return true;
    }

    private static IReadOnlyList<object?> UnwrapValues(IReadOnlyList<object?> values)
    {
        return values
            .Select(value => value is ToshClassSelfReference self ? self.Unwrap() : value)
            .ToArray();
    }

    private static object? FlattenCallResult(IReadOnlyList<object?> values)
    {
        var unwrapped = UnwrapValues(values);
        return unwrapped.Count switch
        {
            0 => null,
            1 => unwrapped[0],
            _ => unwrapped.ToArray(),
        };
    }

    private IReadOnlyList<ToshClassConstructorDefinition> GetConstructorDefinitions()
    {
        var constructors = new List<ToshClassConstructorDefinition>(_constructors);

        if (_primaryConstructorParameters.Count > 0)
        {
            constructors.Add(new ToshClassConstructorDefinition(
                _primaryConstructorParameters,
                new BlockSyntax(Array.Empty<Parsing.StatementSyntax>(), Span),
                SourceName,
                SourceText,
                Span,
                CapturedScopes));
        }

        if (constructors.Count == 0)
        {
            constructors.Add(new ToshClassConstructorDefinition(
                Array.Empty<FunctionParameterDefinition>(),
                new BlockSyntax(Array.Empty<Parsing.StatementSyntax>(), Span),
                SourceName,
                SourceText,
                Span,
                CapturedScopes));
        }

        return constructors;
    }

    private IReadOnlyList<ShellConstructorDescriptor> GetConstructorMetadata()
    {
        return GetConstructorDefinitions()
            .Select(constructor => new ShellConstructorDescriptor(
                constructor.Parameters.Count,
                FormatConstructorSignature(constructor.Parameters)))
            .ToArray();
    }

    private string FormatMethodSignature(ToshClassMethodDefinition method)
    {
        var modifier = method.IsStatic ? "static " : string.Empty;
        return $"{modifier}{GetAnnotationDisplayName(method.ReturnTypeName)} {method.Name}({FormatParameters(method.Parameters)})";
    }

    private string FormatConstructorSignature(IReadOnlyList<FunctionParameterDefinition> parameters)
    {
        return $"{Name}({FormatParameters(parameters)})";
    }

    private static string FormatParameters(IReadOnlyList<FunctionParameterDefinition> parameters)
    {
        return string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                var suffix = parameter.IsOptional ? "?" : string.Empty;
                var rest = parameter.IsRest ? "..." : string.Empty;
                return parameter.TypeName is { Length: > 0 }
                    ? $"{parameter.Name}{suffix}{rest}: {parameter.TypeName}"
                    : $"{parameter.Name}{suffix}{rest}";
            }));
    }

    private static string GetAnnotationDisplayName(string? typeName)
    {
        return string.IsNullOrWhiteSpace(typeName) ? typeof(object).FullName ?? typeof(object).Name : typeName;
    }
}
