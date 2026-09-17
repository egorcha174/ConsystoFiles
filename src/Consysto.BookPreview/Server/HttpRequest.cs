namespace Consysto.BookPreview.Server;

/// <summary>The head of an HTTP/1.x request: the server answers GET and HEAD only, so there is never a body to read.</summary>
internal sealed class HttpRequest
{
    public required string Method { get; init; }

    /// <summary>Percent-decoded.</summary>
    public required string Path { get; init; }

    public required Dictionary<string, string> Query { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public required bool KeepAlive { get; init; }

    public bool IsHead => Method == "HEAD";

    public int Page => int.TryParse(Query.GetValueOrDefault("page"), out var page) && page > 0 ? page : 1;

    public string? QueryValue(string name)
        => Query.GetValueOrDefault(name);

    /// <summary>Null for a malformed request line.</summary>
    public static HttpRequest? Parse(string head)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var target = requestLine[1];
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(target, UriKind.Absolute, out var absolute))
            target = absolute.PathAndQuery;

        if (!target.StartsWith('/'))
            return null;

        var question = target.IndexOf('?');
        var connection = headers.GetValueOrDefault("Connection") ?? string.Empty;
        var hasBody = headers.ContainsKey("Transfer-Encoding") || headers.TryGetValue("Content-Length", out var length) && length != "0";
        var keepAlive = requestLine[2] == "HTTP/1.1"
            ? !connection.Contains("close", StringComparison.OrdinalIgnoreCase)
            : connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase);

        return new HttpRequest
        {
            Method = requestLine[0],
            Path = Unescape(question < 0 ? target : target[..question]),
            Query = ParseQuery(question < 0 ? string.Empty : target[(question + 1)..]),
            Headers = headers,
            // A body would be read as the next request
            KeepAlive = keepAlive && !hasBody,
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Unescape((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            values.TryAdd(name, equals < 0 ? string.Empty : Unescape(pair[(equals + 1)..].Replace('+', ' ')));
        }

        return values;
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
