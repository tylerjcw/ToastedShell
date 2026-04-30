using Tosh.Runtime;

namespace Tosh.Language;

internal sealed class ToshModuleObject : IShellRecordObject, IShellInvocableObject
{
    private readonly ToshEngine _engine;
    private readonly ModuleExportTable _exports;

    public ToshModuleObject(ToshEngine engine, string name, ModuleExportTable exports)
    {
        _engine = engine;
        Name = name;
        _exports = exports;
    }

    public string Name { get; }

    public string ShellTypeName => "module";

    internal NativeLibraryBinding? NativeLibraryBinding { get; init; }

    internal ModuleExportTable ExportTable => _exports;

    internal void SetCommand(IShellCommand command)
    {
        _exports.Commands[command.Name] = command;
    }

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        if (_exports.Modules.TryGetValue(name, out value))
        {
            return true;
        }

        if (_exports.Types.TryGetValue(name, out var type))
        {
            value = type;
            return true;
        }

        if (_exports.Variables.TryGetValue(name, out value))
        {
            return true;
        }

        if (_exports.Commands.TryGetValue(name, out var command))
        {
            value = command;
            return true;
        }

        value = null;
        return false;
    }

    public bool TrySetMember(string name, object? value)
    {
        if (_exports.Variables.ContainsKey(name))
        {
            _exports.Variables[name] = value;
            return true;
        }

        return false;
    }

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
    {
        var members = new List<KeyValuePair<string, object?>>();

        foreach (var entry in _exports.Modules.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            members.Add(new KeyValuePair<string, object?>(entry.Key, entry.Value));
        }

        foreach (var entry in _exports.Types.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            members.Add(new KeyValuePair<string, object?>(entry.Key, entry.Value));
        }

        foreach (var entry in _exports.Variables.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            members.Add(new KeyValuePair<string, object?>(entry.Key, entry.Value));
        }

        foreach (var entry in _exports.Commands.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            members.Add(new KeyValuePair<string, object?>(entry.Key, entry.Value));
        }

        return members;
    }

    public InvocationResult InvokeInstanceMethod(string methodName, IReadOnlyList<object?> arguments)
    {
        if (_exports.Commands.TryGetValue(methodName, out var command))
        {
            var context = new CommandContext(
                _engine.Runtime,
                AsyncEnumerableExtensions.Empty<object?>(),
                arguments,
                CancellationToken.None,
                ScopedTypeResolver: _engine.CreateScopedTypeResolver());
            var values = AsyncEnumerableExtensions.ToListAsync(command.ExecuteAsync(context), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return new InvocationResult(Flatten(values), ReturnedVoid: false);
        }

        throw new InvalidOperationException($"Member '{methodName}' was not found on module '{Name}'.");
    }

    public bool TryGetExport(string name, out object? value)
    {
        return TryGetMember(name, out value);
    }

    private static object? Flatten(IReadOnlyList<object?> values)
    {
        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => values.ToArray(),
        };
    }
}
