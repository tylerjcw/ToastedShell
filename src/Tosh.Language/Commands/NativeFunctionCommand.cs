using System.Collections.Concurrent;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Language.Commands;

internal sealed record NativeFunctionParameterDefinition(string Name, string TypeName, Type ClrType, NativeParameterPassingMode PassingMode)
{
    public Type DelegateParameterType => PassingMode == NativeParameterPassingMode.In
        ? ClrType
        : ClrType.MakeByRefType();
}
internal enum NativeFunctionReturnKind
{
    Default,
    CString,
}

internal sealed record NativeFunctionReturnDefinition(string TypeName, Type ClrType, NativeFunctionReturnKind Kind);

internal sealed class NativeFunctionCommand : IShellCommand, ICommandResolutionMetadata
{
    private readonly Delegate _delegate;
    private readonly IReadOnlyList<NativeFunctionParameterDefinition> _parameters;
    private readonly NativeFunctionReturnDefinition? _return;

    public NativeFunctionCommand(
        string moduleName,
        string name,
        string symbolName,
        NativeLibraryBinding binding,
        IReadOnlyList<NativeFunctionParameterDefinition> parameters,
        NativeFunctionReturnDefinition? @return,
        CallingConvention callingConvention)
    {
        ModuleName = moduleName;
        Name = name;
        SymbolName = symbolName;
        Binding = binding;
        _parameters = parameters;
        _return = @return;
        ReturnTypeName = @return?.TypeName;
        _delegate = CreateDelegate(binding.Handle, symbolName, parameters, @return?.ClrType, callingConvention);
    }

    public string ModuleName { get; }

    public string Name { get; }

    public string SymbolName { get; }

    public NativeLibraryBinding Binding { get; }

    public string? ReturnTypeName { get; }

    public string Description => $"Invokes native export '{SymbolName}' from '{Binding.Target}'.";

    public string Usage => ReturnTypeName is null
        ? $"{ModuleName}.{Name}({string.Join(", ", _parameters.Select(static parameter => parameter.TypeName))})"
        : $"{ModuleName}.{Name}({string.Join(", ", _parameters.Select(static parameter => parameter.TypeName))}) -> {ReturnTypeName}";

    public CommandResolutionKind ResolutionKind => CommandResolutionKind.Function;

    public async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count != _parameters.Count)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_argument_count_mismatch",
                title: $"Native function '{ModuleName}.{Name}' expects {_parameters.Count} argument(s) but received {context.Arguments.Count}.",
                label: $"'{ModuleName}.{Name}' requires {_parameters.Count} argument(s)");
        }

        var convertedArguments = new object?[_parameters.Count];
        var bufferBackedParameters = new List<(int Index, NativeFunctionParameterDefinition Parameter, NativeBuffer Buffer)>();

        for (var index = 0; index < _parameters.Count; index++)
        {
            var parameter = _parameters[index];
            var value = context.Arguments[index];

            if (parameter.PassingMode != NativeParameterPassingMode.In && value is NativeBuffer nativeBuffer)
            {
                convertedArguments[index] = parameter.PassingMode == NativeParameterPassingMode.Out
                    ? NativeInteropUtilities.CreateDefaultValue(parameter.ClrType)
                    : NativeInteropUtilities.ReadValue(parameter.ClrType, nativeBuffer.Pointer);
                bufferBackedParameters.Add((index, parameter, nativeBuffer));
                continue;
            }

            if (parameter.PassingMode != NativeParameterPassingMode.In && value is null)
            {
                convertedArguments[index] = NativeInteropUtilities.CreateDefaultValue(parameter.ClrType);
                continue;
            }

            if (!TypeConversion.TryConvert(value, parameter.ClrType, out var converted))
            {
                throw context.CreateDiagnostic(
                    code: "tosh::runtime::native_argument_type_conversion_failed",
                    title: $"Argument {index + 1} for native function '{ModuleName}.{Name}' could not be converted to '{parameter.TypeName}'.",
                    argumentIndex: index,
                    label: $"argument {index + 1} expects {parameter.TypeName}");
            }

            convertedArguments[index] = converted;
        }

        object? result;

        try
        {
            result = _delegate.DynamicInvoke(convertedArguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(exception.InnerException.Message, exception.InnerException);
        }

        foreach (var bufferBackedParameter in bufferBackedParameters)
        {
            NativeInteropUtilities.WriteValue(bufferBackedParameter.Buffer.Pointer, convertedArguments[bufferBackedParameter.Index]);
        }

        var byRefParameters = _parameters
            .Select((parameter, index) => (Parameter: parameter, Index: index))
            .Where(static entry => entry.Parameter.PassingMode != NativeParameterPassingMode.In)
            .ToArray();

        if ((_return is null || _return.ClrType == typeof(void)) && byRefParameters.Length == 0)
        {
            yield break;
        }

        if ((_return is null || _return.ClrType == typeof(void)) && byRefParameters.Length == 1)
        {
            yield return convertedArguments[byRefParameters[0].Index];
            yield break;
        }

        if (_return is not null && _return.ClrType != typeof(void) && byRefParameters.Length == 0)
        {
            yield return ConvertReturn(result, _return);
            yield break;
        }

        yield return CreateCompositeResult(result, convertedArguments, byRefParameters, _return);
        await Task.CompletedTask;
    }

    private static object CreateCompositeResult(
        object? returnValue,
        IReadOnlyList<object?> convertedArguments,
        IReadOnlyList<(NativeFunctionParameterDefinition Parameter, int Index)> byRefParameters,
        NativeFunctionReturnDefinition? @return)
    {
        IDictionary<string, object?> fields = new ExpandoObject();

        if (@return is not null && @return.ClrType != typeof(void))
        {
            fields["ReturnValue"] = ConvertReturn(returnValue, @return);
        }

        foreach (var (parameter, index) in byRefParameters)
        {
            fields[parameter.Name] = convertedArguments[index];
        }

        return fields;
    }

    private static object? ConvertReturn(object? result, NativeFunctionReturnDefinition @return)
    {
        return @return.Kind switch
        {
            NativeFunctionReturnKind.CString => result is IntPtr pointer
                ? (pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer))
                : result,
            _ => result,
        };
    }

    private static Delegate CreateDelegate(
        IntPtr libraryHandle,
        string symbolName,
        IReadOnlyList<NativeFunctionParameterDefinition> parameters,
        Type? returnType,
        CallingConvention callingConvention)
    {
        var export = NativeLibrary.GetExport(libraryHandle, symbolName);
        var delegateType = NativeDelegateTypeFactory.GetOrCreate(
            parameters,
            returnType ?? typeof(void),
            callingConvention);
        return Marshal.GetDelegateForFunctionPointer(export, delegateType);
    }
}

internal static class NativeDelegateTypeFactory
{
    private static readonly AssemblyBuilder Assembly = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName("Tosh.NativeDelegates"),
        AssemblyBuilderAccess.Run);
    private static readonly ModuleBuilder Module = Assembly.DefineDynamicModule("Tosh.NativeDelegates");
    private static readonly ConcurrentDictionary<string, Type> Cache = new(StringComparer.Ordinal);
    private static int _nextTypeId;

    public static Type GetOrCreate(IReadOnlyList<NativeFunctionParameterDefinition> parameters, Type returnType, CallingConvention callingConvention)
    {
        var key = string.Join("|", parameters.Select(static parameter => $"{parameter.DelegateParameterType.AssemblyQualifiedName}@{parameter.PassingMode}"))
                  + "->"
                  + returnType.AssemblyQualifiedName
                  + "@"
                  + callingConvention;

        return Cache.GetOrAdd(key, _ => Create(parameters, returnType, callingConvention));
    }

    private static Type Create(IReadOnlyList<NativeFunctionParameterDefinition> parameters, Type returnType, CallingConvention callingConvention)
    {
        var typeBuilder = Module.DefineType(
            "ToshNativeDelegate" + Interlocked.Increment(ref _nextTypeId),
            TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AnsiClass | TypeAttributes.AutoClass,
            typeof(MulticastDelegate));

        var attributeConstructor = typeof(UnmanagedFunctionPointerAttribute).GetConstructor([typeof(CallingConvention)])
                                   ?? throw new InvalidOperationException("Unable to locate UnmanagedFunctionPointerAttribute constructor.");
        typeBuilder.SetCustomAttribute(new CustomAttributeBuilder(attributeConstructor, [callingConvention]));

        var constructor = typeBuilder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            [typeof(object), typeof(IntPtr)]);
        constructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        var invoke = typeBuilder.DefineMethod(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            returnType,
            parameters.Select(static parameter => parameter.DelegateParameterType).ToArray());
        invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            var attributes = parameter.PassingMode switch
            {
                NativeParameterPassingMode.Out => ParameterAttributes.Out,
                NativeParameterPassingMode.Ref => ParameterAttributes.In | ParameterAttributes.Out,
                _ => ParameterAttributes.None,
            };
            invoke.DefineParameter(index + 1, attributes, parameter.Name);
        }

        return typeBuilder.CreateType()
               ?? throw new InvalidOperationException("Failed to create native delegate type.");
    }
}
