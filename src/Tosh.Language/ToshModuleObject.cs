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

    /// <summary>
    /// Whether this module was declared <c>partial</c>, so a later partial
    /// declaration may extend it.
    /// </summary>
    /// <remarks>
    /// Classes, records and structs all refuse a partial declaration that would
    /// extend a non-partial original. Modules accepted it silently until this was
    /// tracked, which is the one place the four declaration kinds disagreed.
    /// </remarks>
    internal bool IsPartial { get; set; }

    internal void SetCommand(IShellCommand command)
    {
        _exports.Commands[command.Name] = command;
    }

    /// <summary>
    /// The emitted CLR type for a <c>raw struct</c> exported from this module,
    /// so `ToastLib.System.SysInfo` is nameable in a native signature declared
    /// outside the module. Kept separate from <see cref="TryGetMember"/>, which
    /// yields the <c>IShellNamedType</c> façade rather than the type itself.
    /// </summary>
    internal bool TryGetExportedNativeType(string name, out Type? type) =>
        _exports.NativeTypes.TryGetValue(name, out type);

    internal bool TryGetExportedModule(string name, out ToshModuleObject module)
    {
        if (_exports.Modules.TryGetValue(name, out var value) && value is ToshModuleObject nested)
        {
            module = nested;
            return true;
        }

        module = null!;
        return false;
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

        // A module named after a CLR type shadows it, so `Math.Clamp(…)` inside
        // `module Math` looked the method up on the module and failed. Falling back to the
        // shadowed type on a member *miss* keeps the module's own exports winning while
        // leaving the CLR surface reachable — the same resolution rule as TS-P2-37's
        // `file`/`System.IO.File` collision.
        //
        // Only reached once the module has no such export, so this cannot change which
        // member an existing call resolves to; it only turns a hard failure into a hit.
        if (_engine.TryResolveTypeName(Name) is { } shadowedType)
        {
            return _engine.LanguageRuntime.Invoker.InvokeStatic(shadowedType, methodName, arguments);
        }

        throw new InvalidOperationException(
            $"Member '{methodName}' was not found on module '{Name}'.");
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
