using System.Runtime.InteropServices;

namespace Tosh.Core.Commands;

public sealed class NativeOffsetOfCommand : ShellCommand
{
    public NativeOffsetOfCommand(string name = "native-offsetof")
        : base(name, "Returns the unmanaged field offset for a sequential or explicit-layout struct.", $"{name} <type-name>[.<field-name>] [field-name]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count is < 1 or > 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_offsetof_argument_count",
                title: $"{Name} expects a type name and a field name.",
                label: $"write something like '{Name} Tosh.Tests.NativePoint Y' or '{Name} Tosh.Tests.NativePoint.Y'");
        }

        var typeArgument = context.Arguments[0]?.ToString()?.Trim();
        string? fieldName;
        Type type;

        if (context.Arguments.Count == 1)
        {
            if (string.IsNullOrWhiteSpace(typeArgument) || !TrySplitQualifiedFieldPath(typeArgument, out var typeName, out fieldName))
            {
                throw context.CreateDiagnostic(
                    code: "tosh::runtime::native_offsetof_requires_field_name",
                    title: $"{Name} requires a field name.",
                    argumentIndex: 0,
                    label: "use '<type>.<field>' or pass the field name as a second argument");
            }

            type = NativeCommandUtilities.ResolveInteropType(context, typeName, 0, allowString: false);
        }
        else
        {
            type = NativeCommandUtilities.ResolveInteropType(context, context.Arguments[0], 0, allowString: false);
            fieldName = context.Arguments[1]?.ToString();
        }

        if (!NativeInteropUtilities.IsStructLayoutType(type))
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_offsetof_requires_struct_layout_type",
                title: $"'{type.FullName ?? type.Name}' is not a sequential or explicit-layout struct.",
                argumentIndex: 0,
                label: "use a struct with [StructLayout(LayoutKind.Sequential)] or [StructLayout(LayoutKind.Explicit)]");
        }

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::native_offsetof_requires_field_name",
                title: $"{Name} requires a field name.",
                argumentIndex: context.Arguments.Count == 1 ? 0 : 1,
                label: "write a public field name here");
        }

        yield return Marshal.OffsetOf(type, fieldName.Trim()).ToInt64();
        await Task.CompletedTask;
    }

    private static bool TrySplitQualifiedFieldPath(string text, out string typeName, out string fieldName)
    {
        var separatorIndex = text.LastIndexOf('.');

        if (separatorIndex <= 0 || separatorIndex == text.Length - 1)
        {
            typeName = string.Empty;
            fieldName = string.Empty;
            return false;
        }

        typeName = text[..separatorIndex].Trim();
        fieldName = text[(separatorIndex + 1)..].Trim();
        return typeName.Length > 0 && fieldName.Length > 0;
    }
}
