using System.Text;

namespace Tosh.Core;

public static class DelimitedParser
{
    public static IReadOnlyList<string[]> Parse(string text, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return Array.Empty<string[]>();
        }

        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (character == '"')
            {
                inQuotes = true;
                continue;
            }

            if (character == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (character == '\r')
            {
                fields.Add(field.ToString());
                field.Clear();
                AddRecord(records, fields);

                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            if (character == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                AddRecord(records, fields);
                continue;
            }

            field.Append(character);
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("Input ended while still inside a quoted field.");
        }

        fields.Add(field.ToString());
        AddRecord(records, fields);
        return records;
    }

    private static void AddRecord(ICollection<string[]> records, List<string> fields)
    {
        if (fields.Count == 1 && fields[0].Length == 0)
        {
            fields.Clear();
            return;
        }

        records.Add(fields.ToArray());
        fields.Clear();
    }
}
