namespace Tosh.Runtime;

public interface IShellExecHandler
{
    Task<ShellExecResult> ExecuteAsync(ShellExecRequest request, CancellationToken cancellationToken);
}
