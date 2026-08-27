namespace Tosh.Runtime;

/// <summary>A configuration section that can be returned to its defaults.</summary>
/// <remarks>
/// `TOAST-0006`. Declared alongside the language because two of its implementors are:
/// the hushed-diagnostic list, since `hush` is a language feature, and the directory-alias
/// table, which language-side path resolution reads. Loading either from a configuration
/// file remains the shell's job.
/// </remarks>
public interface IResettableShellConfig
{
    void Reset();
}
