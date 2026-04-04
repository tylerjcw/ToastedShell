namespace Tosh.Core;

public interface IDisplayTreeNode
{
    IEnumerable<object> GetDisplayChildren();
}
