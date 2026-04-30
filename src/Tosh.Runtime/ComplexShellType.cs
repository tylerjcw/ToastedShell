using System.Collections;
using System.Globalization;
using System.Numerics;

namespace Tosh.Runtime;

/// <summary>
/// Shell static type for <c>Complex</c> numbers.
/// Exposes construction helpers and common complex-number operations.
/// </summary>
public sealed class ComplexShellType : IShellNamedType
{
    private static readonly IReadOnlyList<ShellMemberDescriptor> Members =
    [
        new("Real", "Property", "System.Double", IsStatic: false, IsWritable: false),
        new("Imaginary", "Property", "System.Double", IsStatic: false, IsWritable: false),
        new("Magnitude", "Property", "System.Double", IsStatic: false, IsWritable: false),
        new("Phase", "Property", "System.Double", IsStatic: false, IsWritable: false),
        new("Zero", "Property", "Complex", IsStatic: true, IsWritable: false),
        new("One", "Property", "Complex", IsStatic: true, IsWritable: false),
        new("ImaginaryOne", "Property", "Complex", IsStatic: true, IsWritable: false),
    ];

    private static readonly IReadOnlyList<ShellMethodDescriptor> Methods =
    [
        new("Conjugate", "Complex", IsStatic: false, ParameterCount: 0, Signature: "Complex Conjugate()"),
        new("from-polar", "Complex", IsStatic: true, ParameterCount: 2, Signature: "static Complex from-polar(System.Double magnitude, System.Double phase)"),
        new("conjugate", "Complex", IsStatic: true, ParameterCount: 1, Signature: "static Complex conjugate(Complex value)"),
        new("magnitude", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double magnitude(Complex value)"),
        new("phase", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double phase(Complex value)"),
        new("real", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double real(Complex value)"),
        new("imaginary", "System.Double", IsStatic: true, ParameterCount: 1, Signature: "static System.Double imaginary(Complex value)"),
    ];

    private static readonly IReadOnlyList<ShellConstructorDescriptor> Constructors =
    [
        new(0, "Complex()"),
        new(1, "Complex(real | [real, imaginary])"),
        new(2, "Complex(real, imaginary)"),
    ];

    public static readonly ComplexShellType Instance = new();

    public string ShellTypeName => "Complex";

    public string ShellFullName => "ToSh.Complex";

    public string? ShellNamespace => "ToSh";

    public string? ShellAssemblyName => "ToSh";

    public string? ShellBaseTypeName => typeof(ValueType).FullName;

    public bool ShellIsClass => false;

    public bool ShellIsInterface => false;

    public bool ShellIsEnum => false;

    public bool ShellIsValueType => true;

    public bool ShellIsAbstract => false;

    public bool ShellIsGenericType => false;

    public bool ShellIsArray => false;

    public bool ShellIsPublic => true;

    public object CreateInstance(IReadOnlyList<object?> arguments)
    {
        return arguments.Count switch
        {
            0 => Complex.Zero,
            1 => FromValue(arguments[0]),
            2 => new Complex(ToDouble(arguments[0], 0), ToDouble(arguments[1], 1)),
            _ => throw new InvalidOperationException($"Complex expected 0, 1, or 2 argument(s), got {arguments.Count}."),
        };
    }

    public InvocationResult InvokeStaticMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        object? result = methodName.ToLowerInvariant() switch
        {
            "from-polar" or "frompolar" => (object)Complex.FromPolarCoordinates(ToDouble(arguments, 0), ToDouble(arguments, 1)),
            "conjugate" => (object)Complex.Conjugate(ToComplex(arguments, 0)),
            "magnitude" => (object)ToComplex(arguments, 0).Magnitude,
            "phase" => (object)ToComplex(arguments, 0).Phase,
            "real" => (object)ToComplex(arguments, 0).Real,
            "imaginary" => (object)ToComplex(arguments, 0).Imaginary,
            _ => throw new InvalidOperationException(
                $"Complex.{methodName} is not a recognized function. Available: from-polar, conjugate, magnitude, phase, real, imaginary."),
        };

        return new InvocationResult(result, false);
    }

    public bool TryGetStaticMember(string memberName, out object? value)
    {
        value = memberName.ToLowerInvariant() switch
        {
            "zero" => Complex.Zero,
            "one" => Complex.One,
            "imaginaryone" => Complex.ImaginaryOne,
            "from-polar" or "frompolar" => new StaticComplexCallable("Complex.from-polar", 2, 2, arguments =>
                Complex.FromPolarCoordinates(ToDouble(arguments, 0), ToDouble(arguments, 1))),
            "conjugate" => new StaticComplexCallable("Complex.conjugate", 1, 1, arguments =>
                Complex.Conjugate(ToComplex(arguments, 0))),
            "magnitude" => new StaticComplexCallable("Complex.magnitude", 1, 1, arguments =>
                ToComplex(arguments, 0).Magnitude),
            "phase" => new StaticComplexCallable("Complex.phase", 1, 1, arguments =>
                ToComplex(arguments, 0).Phase),
            "real" => new StaticComplexCallable("Complex.real", 1, 1, arguments =>
                ToComplex(arguments, 0).Real),
            "imaginary" => new StaticComplexCallable("Complex.imaginary", 1, 1, arguments =>
                ToComplex(arguments, 0).Imaginary),
            _ => null,
        };

        return value is not null;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        value = name switch
        {
            "Name" => ShellTypeName,
            "FullName" => ShellFullName,
            "Namespace" => ShellNamespace,
            "Assembly" => ShellAssemblyName,
            "BaseType" => ShellBaseTypeName,
            "IsClass" => ShellIsClass,
            "IsInterface" => ShellIsInterface,
            "IsEnum" => ShellIsEnum,
            "IsValueType" => ShellIsValueType,
            "IsAbstract" => ShellIsAbstract,
            "IsGenericType" => ShellIsGenericType,
            "IsArray" => ShellIsArray,
            "IsPublic" => ShellIsPublic,
            "PropertyCount" => Members.Count(member => !member.IsStatic),
            "MethodCount" => Methods.Count(method => !method.IsStatic),
            "StaticMethodCount" => Methods.Count(method => method.IsStatic),
            "ConstructorCount" => Constructors.Count,
            _ => null,
        };

        return value is not null;
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false) =>
    [
        new("Name", ShellTypeName),
        new("FullName", ShellFullName),
        new("Namespace", ShellNamespace),
        new("Assembly", ShellAssemblyName),
        new("BaseType", ShellBaseTypeName),
        new("IsClass", ShellIsClass),
        new("IsInterface", ShellIsInterface),
        new("IsEnum", ShellIsEnum),
        new("IsValueType", ShellIsValueType),
        new("IsAbstract", ShellIsAbstract),
        new("IsGenericType", ShellIsGenericType),
        new("IsArray", ShellIsArray),
        new("IsPublic", ShellIsPublic),
        new("PropertyCount", Members.Count(member => !member.IsStatic)),
        new("MethodCount", Methods.Count(method => !method.IsStatic)),
        new("StaticMethodCount", Methods.Count(method => method.IsStatic)),
        new("ConstructorCount", Constructors.Count),
    ];

    public IReadOnlyList<ShellMemberDescriptor> GetShellMembers(bool includeHidden = false) => Members;

    public IReadOnlyList<ShellMethodDescriptor> GetShellMethods(bool includeHidden = false) => Methods;

    public IReadOnlyList<ShellConstructorDescriptor> GetShellConstructors() => Constructors;

    internal static Complex FromValue(object? value)
    {
        if (TryConvert(value, out var complex))
        {
            return complex;
        }

        throw new InvalidOperationException($"Expected a Complex-compatible value, got {value?.GetType().Name ?? "null"}.");
    }

    internal static bool TryConvert(object? value, out Complex complex)
    {
        switch (value)
        {
            case null:
                complex = default;
                return false;

            case Complex existing:
                complex = existing;
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                complex = new Complex(Convert.ToDouble(value, CultureInfo.InvariantCulture), 0d);
                return true;

            case string text when TryParseString(text, out complex):
                return true;

            case ToshVector vector when TryConvertComponents(vector, out complex):
                return true;

            case IEnumerable enumerable when value is not string && value is not IDictionary && TryConvertComponents(enumerable, out complex):
                return true;

            default:
                complex = default;
                return false;
        }
    }

    internal static string FormatCompact(Complex value)
    {
        var sign = value.Imaginary >= 0 ? "+" : "-";
        return $"{value.Real.ToString(CultureInfo.InvariantCulture)} {sign} {Math.Abs(value.Imaginary).ToString(CultureInfo.InvariantCulture)}i";
    }

    private static Complex ToComplex(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
        {
            throw new InvalidOperationException($"Complex function expected at least {index + 1} argument(s), got {arguments.Count}.");
        }

        return FromValue(arguments[index]);
    }

    private static double ToDouble(IReadOnlyList<object?> arguments, int index)
    {
        if (index >= arguments.Count)
        {
            throw new InvalidOperationException($"Complex function expected at least {index + 1} argument(s), got {arguments.Count}.");
        }

        return ToDouble(arguments[index], index);
    }

    private static double ToDouble(object? value, int argIndex)
    {
        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Expected numeric argument at position {argIndex}, got {value?.GetType().Name ?? "null"}.", ex);
        }
    }

    private static bool TryParseString(string text, out Complex complex)
    {
        var normalized = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);

        if (normalized.Length == 0)
        {
            complex = default;
            return false;
        }

        if (!normalized.Contains('i', StringComparison.OrdinalIgnoreCase))
        {
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var realOnly))
            {
                complex = new Complex(realOnly, 0d);
                return true;
            }

            complex = default;
            return false;
        }

        if (normalized[^1] is not ('i' or 'I'))
        {
            complex = default;
            return false;
        }

        var withoutSuffix = normalized[..^1];
        if (withoutSuffix.Length == 0)
        {
            complex = default;
            return false;
        }

        var separatorIndex = FindRealImaginarySeparator(withoutSuffix);
        if (separatorIndex > 0)
        {
            var realPart = withoutSuffix[..separatorIndex];
            var imaginaryPart = withoutSuffix[separatorIndex..];

            if (TryParseRealPart(realPart, out var real) && TryParseImaginaryPart(imaginaryPart, out var imaginary))
            {
                complex = new Complex(real, imaginary);
                return true;
            }

            complex = default;
            return false;
        }

        if (TryParseImaginaryPart(withoutSuffix, out var pureImaginary))
        {
            complex = new Complex(0d, pureImaginary);
            return true;
        }

        complex = default;
        return false;
    }

    private static int FindRealImaginarySeparator(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] is '+' or '-' && text[i - 1] is not ('e' or 'E'))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseRealPart(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseImaginaryPart(string text, out double value)
    {
        if (text == "+")
        {
            value = 1d;
            return true;
        }

        if (text == "-")
        {
            value = -1d;
            return true;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryConvertComponents(IEnumerable values, out Complex complex)
    {
        var components = new List<double>(2);

        foreach (var item in values)
        {
            if (item is null)
            {
                complex = default;
                return false;
            }

            if (components.Count == 2)
            {
                complex = default;
                return false;
            }

            try
            {
                components.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture));
            }
            catch
            {
                complex = default;
                return false;
            }
        }

        if (components.Count == 1)
        {
            complex = new Complex(components[0], 0d);
            return true;
        }

        if (components.Count == 2)
        {
            complex = new Complex(components[0], components[1]);
            return true;
        }

        complex = default;
        return false;
    }

    private sealed class StaticComplexCallable(
        string callableName,
        int requiredParameterCount,
        int? maximumParameterCount,
        Func<IReadOnlyList<object?>, object?> handler) : IShellCallable
    {
        public string CallableName => callableName;

        public int RequiredParameterCount => requiredParameterCount;

        public int? MaximumParameterCount => maximumParameterCount;

        public async IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Arguments.Count < requiredParameterCount)
            {
                throw new InvalidOperationException(
                    $"{callableName} expects at least {requiredParameterCount} argument(s), got {context.Arguments.Count}.");
            }

            if (maximumParameterCount is int max && context.Arguments.Count > max)
            {
                throw new InvalidOperationException(
                    $"{callableName} expects at most {max} argument(s), got {context.Arguments.Count}.");
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            yield return handler(context.Arguments);
            await Task.CompletedTask;
        }
    }
}
