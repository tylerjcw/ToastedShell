namespace Tosh.LspFixture;

public sealed class Widget
{
    public Widget()
    {
        Name = string.Empty;
    }

    public Widget(string name, int count = 1)
    {
        Name = name;
        Count = count;
    }

    public string Name { get; set; }

    public int Count { get; set; }

    public string Rename(string name)
    {
        Name = name;
        return Name;
    }

    public string BuildLabel(string prefix, string suffix = "")
    {
        return $"{prefix}{Name}{suffix}";
    }

    public static Widget Create(string name, int count)
    {
        return new Widget(name, count);
    }
}
