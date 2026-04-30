namespace Tosh.Runtime;

public interface IDisplayTreeNode
{
    IEnumerable<object> GetDisplayChildren();
}
