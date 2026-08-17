namespace Tosh.Runtime;

/// <summary>
/// The value-to-text entry point for emitted code and for runtime paths that carry no
/// <see cref="ToshRuntime"/>.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0014` stage 4. This built its own <see cref="ObjectFormatter"/> from a **fresh**
/// <c>DisplayPreferences</c>, while the interpreter used the shell's live one — so a
/// compiled program and an interpreted one agreed only while nothing was configured, and
/// nothing compared them. It now renders through <see cref="ToastRenderer"/>, which is the
/// same thing the interpreter reaches, so there is one answer rather than two that happen
/// to coincide.
/// </para>
/// <para>
/// The type is kept rather than folded away because <c>BoundUnitEmitter</c> binds to it by
/// reflection when it emits an interpolation — it is the stable symbol emitted IL calls,
/// and moving that is an emitter change with nothing to gain.
/// </para>
/// </remarks>
public static class ToshValueFormatter
{
    /// <summary>Renders one value with Tōast's specified default.</summary>
    public static string Format(object? value) => ToastRenderer.Render(value);
}
