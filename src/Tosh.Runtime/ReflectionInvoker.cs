using System.Reflection;

namespace Tosh.Runtime;

public sealed class ReflectionInvoker
{
    public object CreateInstance(Type type, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);

        CandidateBinding? bestBinding = null;
        ConstructorInfo? bestConstructor = null;

        foreach (var constructor in type.GetConstructors())
        {
            if (!TryBindParameters(constructor.GetParameters(), arguments, out var binding))
            {
                continue;
            }

            if (bestBinding is null || binding.Score < bestBinding.Value.Score)
            {
                bestBinding = binding;
                bestConstructor = constructor;
            }
        }

        if (bestConstructor is not null && bestBinding is not null)
        {
            return bestConstructor.Invoke(bestBinding.Value.BoundArguments);
        }

        throw new InvalidOperationException($"No constructor matched '{type.FullName}' with {arguments.Count} argument(s).");
    }

    public InvocationResult InvokeInstance(object target, string methodName, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        if (target is IShellInvocableObject shellInvocable)
        {
            return shellInvocable.InvokeInstanceMethod(methodName, arguments);
        }

        if (target is IShellStaticType shellStaticType)
        {
            return shellStaticType.InvokeStaticMethod(methodName, arguments);
        }

        if (target is Type staticType)
        {
            return InvokeStatic(staticType, methodName, arguments);
        }

        return Invoke(
            target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public),
            target,
            methodName,
            arguments,
            $"instance method '{methodName}' on '{target.GetType().FullName}'");
    }

    public InvocationResult InvokeStatic(Type type, string methodName, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        return Invoke(
            type.GetMethods(BindingFlags.Static | BindingFlags.Public),
            target: null,
            methodName,
            arguments,
            $"static method '{methodName}' on '{type.FullName}'");
    }

    public object? GetStaticMember(Type type, string memberName)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
        var property = type.GetProperty(memberName, flags);

        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            return property.GetValue(null);
        }

        var field = type.GetField(memberName, flags);

        if (field is not null)
        {
            return field.GetValue(null);
        }

        throw new InvalidOperationException($"Static member '{memberName}' was not found on type '{type.FullName}'.");
    }

    public InvocationResult InvokeStatic(IShellStaticType type, string methodName, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        return type.InvokeStaticMethod(methodName, arguments);
    }

    public object CreateInstance(IShellStaticType type, IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(arguments);

        return type.CreateInstance(arguments);
    }

    public object? GetStaticMember(IShellStaticType type, string memberName)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(memberName);

        if (type.TryGetStaticMember(memberName, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"Static member '{memberName}' was not found on type '{type.ShellTypeName}'.");
    }

    private static InvocationResult Invoke(
        IEnumerable<MethodInfo> methods,
        object? target,
        string methodName,
        IReadOnlyList<object?> arguments,
        string description)
    {
        CandidateBinding? bestBinding = null;
        MethodInfo? bestMethod = null;

        foreach (var method in methods
                     .Where(candidate => !candidate.ContainsGenericParameters)
                     .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryBindParameters(method.GetParameters(), arguments, out var binding))
            {
                continue;
            }

            if (bestBinding is null || binding.Score < bestBinding.Value.Score)
            {
                bestBinding = binding;
                bestMethod = method;
            }
        }

        if (bestMethod is null || bestBinding is null)
        {
            throw new InvalidOperationException($"No overload matched {description} with {arguments.Count} argument(s).");
        }

        var value = bestMethod.Invoke(target, bestBinding.Value.BoundArguments);
        return new InvocationResult(value, bestMethod.ReturnType == typeof(void));
    }

    private static bool TryBindParameters(
        IReadOnlyList<ParameterInfo> parameters,
        IReadOnlyList<object?> rawArguments,
        out CandidateBinding binding)
    {
        var hasParamArray = parameters.Count > 0 && IsParamArray(parameters[^1]);
        var requiredCount = parameters.Count(parameter => !parameter.IsOptional && !IsParamArray(parameter));

        if (rawArguments.Count < requiredCount)
        {
            binding = default;
            return false;
        }

        if (!hasParamArray && rawArguments.Count > parameters.Count)
        {
            binding = default;
            return false;
        }

        var boundArguments = new object?[parameters.Count];
        var score = 0;
        var rawIndex = 0;

        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];

            if (hasParamArray && index == parameters.Count - 1)
            {
                if (!TryBindParamArray(parameter, rawArguments, rawIndex, out var paramArrayValue, out var paramArrayScore))
                {
                    binding = default;
                    return false;
                }

                boundArguments[index] = paramArrayValue;
                score += paramArrayScore;
                rawIndex = rawArguments.Count;
                continue;
            }

            if (rawIndex < rawArguments.Count)
            {
                if (!TryConvertWithScore(rawArguments[rawIndex], parameter.ParameterType, out var converted, out var conversionScore))
                {
                    binding = default;
                    return false;
                }

                boundArguments[index] = converted;
                score += conversionScore;
                rawIndex++;
                continue;
            }

            if (!parameter.IsOptional)
            {
                binding = default;
                return false;
            }

            boundArguments[index] = GetDefaultValue(parameter);
            score += 4;
        }

        if (rawIndex != rawArguments.Count)
        {
            binding = default;
            return false;
        }

        binding = new CandidateBinding(boundArguments, score);
        return true;
    }

    private static bool TryBindParamArray(
        ParameterInfo parameter,
        IReadOnlyList<object?> rawArguments,
        int rawIndex,
        out object? value,
        out int score)
    {
        var parameterType = parameter.ParameterType;
        var elementType = parameterType.GetElementType()
                          ?? throw new InvalidOperationException($"Param-array parameter '{parameter.Name}' is missing an element type.");
        var remainingCount = rawArguments.Count - rawIndex;

        if (remainingCount == 0)
        {
            value = Array.CreateInstance(elementType, 0);
            score = 2;
            return true;
        }

        if (remainingCount == 1 &&
            TryConvertWithScore(rawArguments[rawIndex], parameterType, out var convertedArray, out var directScore))
        {
            value = convertedArray;
            score = directScore + 1;
            return true;
        }

        var array = Array.CreateInstance(elementType, remainingCount);
        score = 3;

        for (var index = 0; index < remainingCount; index++)
        {
            if (!TryConvertWithScore(rawArguments[rawIndex + index], elementType, out var convertedItem, out var itemScore))
            {
                value = null;
                score = 0;
                return false;
            }

            array.SetValue(convertedItem, index);
            score += itemScore;
        }

        value = array;
        return true;
    }

    private static bool TryConvertWithScore(object? value, Type targetType, out object? converted, out int score)
    {
        if (!TypeConversion.TryConvert(value, targetType, out converted))
        {
            score = 0;
            return false;
        }

        if (value is null)
        {
            score = 0;
            return true;
        }

        var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (effectiveType.IsInstanceOfType(value))
        {
            score = 0;
            return true;
        }

        score = 1;
        return true;
    }

    private static bool IsParamArray(ParameterInfo parameter)
    {
        return parameter.GetCustomAttribute<ParamArrayAttribute>() is not null;
    }

    private static object? GetDefaultValue(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue && parameter.DefaultValue is not DBNull)
        {
            return parameter.DefaultValue;
        }

        var parameterType = parameter.ParameterType;
        return parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
    }

    private readonly record struct CandidateBinding(object?[] BoundArguments, int Score);
}
