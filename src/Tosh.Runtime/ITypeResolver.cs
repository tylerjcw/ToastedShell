namespace Tosh.Runtime;

public interface ITypeResolver
{
    Type? Resolve(string name);

    /// <summary>
    /// The CLR type a differently-cased shell alias name asks for — <c>TS-P2-37</c>, used where a
    /// type is being *used* as a type rather than named in an annotation.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="null"/>, meaning "no opinion, use <see cref="Resolve"/>", so a
    /// resolver that holds no alias table of its own needs no implementation.
    /// </remarks>
    Type? ResolveAliasCaseVariant(string name) => null;
}

public interface IImportingTypeResolver : ITypeResolver
{
    void AddUsing(string path);

    void AddAlias(string alias, string path);
}
