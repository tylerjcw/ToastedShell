namespace Tosh.Core;

public interface IShellExecHandler
{
    Task<ShellExecResult> ExecuteAsync(ShellExecRequest request, CancellationToken cancellationToken);
}
