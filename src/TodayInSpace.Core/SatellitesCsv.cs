using System.Text;

namespace TodayInSpace.Core
{
    // A small CSV reader for CelesTrak's SATCAT file. Today's file has no quoted fields, but an object
    // name could gain a comma one day, so quoted fields (with "" escapes and line breaks) are handled.
    public static class SatellitesCsv
    {
        public static IEnumerable<string[]> Rows(string text)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            bool rowHasData = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (c != '"')
                        field.Append(c);
                    else if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                        quoted = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        quoted = true;
                        rowHasData = true;
                        break;
                    case ',':
                        fields.Add(field.ToString());
                        field.Clear();
                        rowHasData = true;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        // Blank lines (like the one at the end of the file) aren't rows.
                        if (rowHasData || field.Length > 0)
                        {
                            fields.Add(field.ToString());
                            yield return fields.ToArray();
                        }
                        fields.Clear();
                        field.Clear();
                        rowHasData = false;
                        break;
                    default:
                        field.Append(c);
                        rowHasData = true;
                        break;
                }
            }

            if (rowHasData || field.Length > 0)
            {
                fields.Add(field.ToString());
                yield return fields.ToArray();
            }
        }
    }
}
