using System.Text;

namespace docker_launcher;

public static class Util
{
    public static bool ContainsPlaceholder(string body, string placeholderName)
    {
        return body.Contains($"#!>> {placeholderName}") && body.Contains($"#!<< {placeholderName}");
    }

    public static void ReplaceTextBetween(ref string body, string placeholderName, string text)
    {
        var start = $"#!>> {placeholderName}";
        var end = $"#!<< {placeholderName}";
        var startIndex = body.IndexOf(start);
        var endIndex = body.IndexOf(end);
        if (startIndex == -1 || endIndex == -1) {
            throw new InvalidOperationException($"Could not find placeholder {placeholderName}");
        }

        // normalize the start index to the beginning of the next line and end index to the end of the previous line
        startIndex = body.LastIndexOf('\n', startIndex);
        endIndex = body.IndexOf('\n', endIndex);

        body = body.Remove(startIndex, endIndex - startIndex);
        body = body.Insert(startIndex, text.TrimEnd());
    }

    public static bool EqualsNoCase(this string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}