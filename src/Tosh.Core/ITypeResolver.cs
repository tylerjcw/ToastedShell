namespace Tosh.Core;

public interface ITypeResolver
{
    Type? Resolve(string name);
}

public interface IImportingTypeResolver : ITypeResolver
{
    void AddUsing(string path);

    void AddAlias(string alias, string path);
}
