using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>The live evaluator state exposed to a host as <c>$tosh.Script</c>.</summary>
internal sealed class ToshScriptNamespace(ToshEngine engine) : IToastScriptNamespace
{
    public string Path => engine.GetCurrentScriptPath();

    public string Name
    {
        get
        {
            var path = Path;
            return string.IsNullOrEmpty(path) ? string.Empty : System.IO.Path.GetFileName(path);
        }
    }

    public string Directory
    {
        get
        {
            var path = Path;

            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            return System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        }
    }

    public object?[] Args => engine.GetCurrentScriptArguments().ToArray();

    public string ShellTypeName => "ToshRuntime.Script";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Path):
                value = Path;
                return true;
            case nameof(Name):
                value = Name;
                return true;
            case nameof(Directory):
                value = Directory;
                return true;
            case nameof(Args):
                value = Args;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Path), Path),
            new(nameof(Name), Name),
            new(nameof(Directory), Directory),
            new(nameof(Args), Args),
        ];
}

/// <summary>The live evaluator state exposed to a host as <c>$tosh.Function</c>.</summary>
internal sealed class ToshFunctionNamespace(ToshEngine engine) : IToastFunctionNamespace
{
    public string Name => engine.GetCurrentFunctionName();

    public object?[] Args => engine.GetCurrentFunctionArguments().ToArray();

    public object? Input => engine.GetCurrentFunctionInput();

    public string ShellTypeName => "ToshRuntime.Function";

    public bool TryGetMember(string name, out object? value, bool includeHidden = false)
    {
        switch (name)
        {
            case nameof(Name):
                value = Name;
                return true;
            case nameof(Args):
                value = Args;
                return true;
            case nameof(Input):
                value = Input;
                return true;
            default:
                value = null;
                return false;
        }
    }

    public bool TrySetMember(string name, object? value) => false;

    public IReadOnlyList<KeyValuePair<string, object?>> GetMembers(bool includeHidden = false)
        =>
        [
            new(nameof(Name), Name),
            new(nameof(Args), Args),
            new(nameof(Input), Input),
        ];
}
