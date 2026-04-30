namespace Tosh.Runtime;

public static class NativeCommandUtilities
{
    public static int ResolveAllocationSize(CommandContext context, object? argument, int argumentIndex)
    {
        if (argument is Type type)
        {
            if (!NativeInteropUtilities.IsSupportedInteropType(type, allowString: false))
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.unsupported_native_allocation_type",
                    title: $"'{type.FullName ?? type.Name}' is not a supported native allocation type.",
                    argumentIndex: argumentIndex,
                    label: "use a primitive CLR type, pointer-sized type, enum, or a struct with sequential/explicit layout");
            }

            return NativeInteropUtilities.SizeOf(type);
        }

        var typeName = argument?.ToString();

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var resolved = context.TypeResolver.Resolve(typeName.Trim());

            if (resolved is not null && NativeInteropUtilities.IsSupportedInteropType(resolved, allowString: false))
            {
                return NativeInteropUtilities.SizeOf(resolved);
            }
        }

        if (TypeConversion.TryConvert(argument, typeof(int), out var convertedSize) &&
            convertedSize is int intSize)
        {
            return intSize;
        }

        throw context.CreateDiagnostic(
            code: "tosh.runtime.native_alloc_requires_size_or_type",
            title: "A native allocation needs a byte count or a supported interop type name.",
            argumentIndex: argumentIndex,
            label: "write something like '256' or 'Tosh.Tests.NativePoint'");
    }

    public static Type ResolveInteropType(CommandContext context, object? argument, int argumentIndex, bool allowString = false)
    {
        var typeName = argument?.ToString();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_type_name_required",
                title: "A native interop type name is required here.",
                argumentIndex: argumentIndex,
                label: "write a CLR type name or imported struct type");
        }

        var resolved = context.TypeResolver.Resolve(typeName.Trim());

        if (resolved is null || !NativeInteropUtilities.IsSupportedInteropType(resolved, allowString))
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.unsupported_native_command_type",
                title: $"Native interop does not support '{typeName}'.",
                argumentIndex: argumentIndex,
                label: $"'{typeName}' is not supported here",
                help: "Use a primitive CLR type, pointer-sized type, enum, or a struct with sequential/explicit layout.");
        }

        return resolved;
    }

    public static IntPtr ResolvePointer(CommandContext context, object? argument, int argumentIndex)
    {
        try
        {
            return NativeInteropUtilities.ResolvePointer(argument);
        }
        catch (Exception exception)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.native_pointer_required",
                title: exception.Message,
                argumentIndex: argumentIndex,
                label: "pass a native buffer, nint, ptr, or other pointer-sized value");
        }
    }
}
