using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Consysto.BookPreview.Library;
using Consysto.Collections;

namespace Consysto.BookPreview.Server;

public sealed class OpdsServerOptions
{
    public int Port { get; init; } = 8765;

    /// <summary>Catalog name the reader shows.</summary>
    public string Title { get; init; } = "Книги";

    /// <summary>Listen on 127.0.0.1 only: for tests, which must not open the port to the network.</summary>
    public bool LoopbackOnly { get; init; }

    /// <summary>
    /// A cover drawn by the host (the flag asks for a thumbnail). Returning null falls back to the image embedded in the book.
    /// </summary>
    public Func<CollectionItem, bool, CancellationToken, Task<byte[]?>>? CoverProvider { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>
/// Serves a <see cref="CollectionIndex"/> as an OPDS 1.2 catalog over plain HTTP, for FBReader, Librera and other readers.
/// Only clients from the local network get an answer; there is no sign-in. The HTTP/1.1 server is its own, small one:
/// the core stays free of ASP.NET, and HTTP.sys would need an administrator to reserve a network address.
/// </summary>
public sealed class OpdsServer : IDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int CopyBufferBytes = 81920;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly CollectionIndex library;
    private readonly OpdsServerOptions options;
    private readonly CatalogBuilder catalog;
    private TcpListener? listener;
    private CancellationTokenSource? cancellation;

    public OpdsServer(CollectionIndex library, OpdsServerOptions options)
    {
        this.library = library;
        this.options = options;
        catalog = new CatalogBuilder(options.Title);
    }

    public int Port => options.Port;

    public bool IsRunning => listener is not null;

    /// <exception cref="SocketException">The port is taken (<see cref="SocketError.AddressAlreadyInUse"/>) or not allowed.</exception>
    public void Start()
    {
        if (listener is not null)
            return;

        var socket = CreateListener();
        socket.Start();
        cancellation = new CancellationTokenSource();
        listener = socket;
        _ = AcceptAsync(socket, cancellation.Token);
    }

    public void Stop()
    {
        cancellation?.Cancel();
        listener?.Stop();
        listener = null;
        cancellation = null;
    }

    public void Dispose()
        => Stop();

    private TcpListener CreateListener()
    {
        TcpListener socket;
        if (options.LoopbackOnly)
        {
            socket = new TcpListener(IPAddress.Loopback, options.Port);
        }
        else if (Socket.OSSupportsIPv6)
        {
            socket = new TcpListener(IPAddress.IPv6Any, options.Port);
            socket.Server.DualMode = true;
        }
        else
        {
            socket = new TcpListener(IPAddress.Any, options.Port);
        }

        // Otherwise a program already listening on one address of the port would share it silently
        socket.ExclusiveAddressUse = true;
        return socket;
    }

    private async Task AcceptAsync(TcpListener socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await socket.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException || ex is SocketException && cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                // The client gave up before the connection was accepted
                continue;
            }

            _ = ServeClientAsync(client, cancellationToken);
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        try
        {
            var stream = client.GetStream();
            if (remote is null || !LocalNetwork.IsLocal(remote))
            {
                options.Log?.Invoke($"refused {remote}: not a local network address");
                await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), cancellationToken);
                return;
            }

            client.NoDelay = true;
            var reader = new RequestReader(stream);
            while (true)
            {
                HttpRequest? request;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    idle.CancelAfter(IdleTimeout);
                    try
                    {
                        request = await reader.ReadAsync(idle.Token);
                    }
                    catch (InvalidDataException)
                    {
                        await WriteAsync(stream, false, Reply.Text(400, "Bad request"), false, cancellationToken);
                        return;
                    }
                }

                if (request is null || !await RespondAsync(stream, request, cancellationToken))
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The reader closed the connection or went idle
        }
        catch (Exception ex)
        {
            options.Log?.Invoke($"connection from {remote} failed: {ex}");
        }
    }

    /// <summary>False when the connection must be closed.</summary>
    private async Task<bool> RespondAsync(Stream stream, HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            await WriteAsync(stream, false, Reply.Text(405, "Method not allowed"), false, cancellationToken);
            return false;
        }

        Reply reply;
        try
        {
            reply = await RouteAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            options.Log?.Invoke($"{request.Path} failed: {ex}");
            reply = Reply.Text(500, "Internal server error");
        }

        if (reply is FileReply file)
            return await WriteFileAsync(stream, request, file.Book, cancellationToken);

        await WriteAsync(stream, request.IsHead, (BytesReply)reply, request.KeepAlive, cancellationToken);
        return request.KeepAlive;
    }

    private async Task<Reply> RouteAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var library = this.library.Snapshot;
        var path = request.Path.Length > 1 ? request.Path.TrimEnd('/') : request.Path;
        var page = request.Page;
        var name = request.QueryValue("name") ?? string.Empty;

        switch (path)
        {
            case "/":
                return new BytesReply(302, "text/plain; charset=utf-8", [], Location: "/opds");
            case "/opds":
                return Feed(catalog.Root(library));
            case "/opds/opensearch.xml":
                return new BytesReply(200, "application/opensearchdescription+xml; charset=utf-8", catalog.OpenSearchDescription(request.Headers.GetValueOrDefault("Host")));
            case "/opds/new":
                return Feed(catalog.Books("/opds/new", [], "Новые поступления", library.Newest, page, "/opds"));
            case "/opds/all":
                return Feed(catalog.Books("/opds/all", [], "Все книги", library.Items, page, "/opds"));
            case "/opds/authors":
                return Feed(catalog.Groups("/opds/authors", "Авторы", ("автор", "автора", "авторов"), library.Authors, request.QueryValue("letter"), "/opds/author"));
            case "/opds/series":
                return Feed(catalog.Groups("/opds/series", "Серии", ("серия", "серии", "серий"), library.Series, request.QueryValue("letter"), "/opds/series-books"));
            case "/opds/genres":
                return Feed(catalog.Groups("/opds/genres", "Жанры", ("жанр", "жанра", "жанров"), library.Genres, request.QueryValue("letter"), "/opds/genre"));
            case "/opds/author":
                return GroupBooks("/opds/author", library.Authors, name, page, "/opds/authors");
            case "/opds/series-books":
                return GroupBooks("/opds/series-books", library.Series, name, page, "/opds/series");
            case "/opds/genre":
                return GroupBooks("/opds/genre", library.Genres, name, page, "/opds/genres");
            case "/opds/folders":
                return catalog.Folder(library, request.QueryValue("path"), page) is { } folder ? Feed(folder) : Reply.NotFound;
            case "/opds/search":
                return Feed(catalog.Search(library, request.QueryValue("q"), page));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && library.Find(Path.GetFileNameWithoutExtension(segments[1])) is { } book)
        {
            switch (segments[0])
            {
                case "book":
                    return new FileReply(book);
                case "cover":
                    return await CoverAsync(book, thumbnail: false, cancellationToken);
                case "thumb":
                    return await CoverAsync(book, thumbnail: true, cancellationToken);
            }
        }

        return Reply.NotFound;
    }

    private Reply GroupBooks(string address, IReadOnlyList<CollectionGroup> groups, string key, int page, string up)
        => groups.FirstOrDefault(group => group.Key == key) is { } found
            ? Feed(catalog.Books(address, [("name", key)], found.Name, found.Items, page, up))
            : Reply.NotFound;

    private async Task<Reply> CoverAsync(CollectionItem book, bool thumbnail, CancellationToken cancellationToken)
    {
        byte[]? image = null;
        if (options.CoverProvider is { } provider)
            image = await provider(book, thumbnail, cancellationToken);

        image ??= await Task.Run(() => ReadEmbeddedCover(book.Path), cancellationToken);
        return image is null
            ? Reply.NotFound
            : new BytesReply(200, BookMediaTypes.OfImage(image), image, CacheControl: "max-age=3600");
    }

    private static byte[]? ReadEmbeddedCover(string path)
    {
        try
        {
            return BookReader.Read(path, includeCover: true)?.Cover;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<bool> WriteFileAsync(Stream stream, HttpRequest request, CollectionItem book, CancellationToken cancellationToken)
    {
        FileStream file;
        try
        {
            file = new FileStream(book.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, CopyBufferBytes, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleted or moved since the scan
            _ = library.ScanAsync();
            await WriteAsync(stream, request.IsHead, Reply.NotFound, request.KeepAlive, cancellationToken);
            return request.KeepAlive;
        }

        await using (file)
        {
            var size = file.Length;
            var (status, start, end) = ParseRange(request.Headers.GetValueOrDefault("Range"), size);
            if (status == 416)
            {
                await WriteHeadAsync(stream, 416, [("Content-Range", $"bytes */{size}"), ("Content-Length", "0")], request.KeepAlive, cancellationToken);
                return request.KeepAlive;
            }

            var format = BookMediaTypes.Of(book);
            var headers = new List<(string, string)>
            {
                ("Content-Type", format.MediaType),
                ("Content-Length", (end - start + 1).ToString(CultureInfo.InvariantCulture)),
                ("Accept-Ranges", "bytes"),
                ("Content-Disposition", ContentDisposition(CatalogBuilder.DownloadName(book))),
                ("Last-Modified", book.Modified.ToString("R", CultureInfo.InvariantCulture)),
            };
            if (status == 206)
                headers.Add(("Content-Range", $"bytes {start}-{end}/{size}"));

            await WriteHeadAsync(stream, status, headers, request.KeepAlive, cancellationToken);
            if (!request.IsHead && end >= start)
            {
                file.Seek(start, SeekOrigin.Begin);
                await CopyAsync(file, stream, end - start + 1, cancellationToken);
                if (start == 0)
                    options.Log?.Invoke($"sent {book.Path}");
            }
        }

        return request.KeepAlive;
    }

    /// <summary>One "bytes=" range; anything else is answered with the whole file, as HTTP allows.</summary>
    private static (int Status, long Start, long End) ParseRange(string? header, long size)
    {
        var whole = (200, 0L, size - 1);
        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
            return whole;

        var range = header["bytes=".Length..].Trim();
        var dash = range.IndexOf('-');
        if (dash < 0)
            return whole;

        var first = range[..dash].Trim();
        var last = range[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix))
                return whole;

            return suffix == 0 || size == 0 ? (416, 0, 0) : (206, Math.Max(0, size - suffix), size - 1);
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            return whole;
        if (start >= size)
            return (416, 0, 0);

        var end = size - 1;
        if (last.Length > 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
                return whole;

            end = Math.Min(end, size - 1);
        }

        return (206, start, end);
    }

    /// <summary>The ASCII name is a transliteration, for readers that ignore the UTF-8 one.</summary>
    private static string ContentDisposition(string fileName)
    {
        var ascii = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            if (character is >= ' ' and < (char)127 and not '"' and not '\\' and not '%')
                ascii.Append(character);
            else
                ascii.Append(Transliterate(character));
        }

        var encoded = Uri.EscapeDataString(fileName).Replace("'", "%27").Replace("(", "%28").Replace(")", "%29").Replace("*", "%2A");
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{encoded}";
    }

    private static string Transliterate(char character)
    {
        const string Cyrillic = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        string[] latin = ["a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "kh", "ts", "ch", "sh", "shch", "", "y", "", "e", "yu", "ya"];

        var index = Cyrillic.IndexOf(char.ToLowerInvariant(character));
        if (index < 0)
            return "_";

        var result = latin[index];
        return char.IsUpper(character) && result.Length > 0 ? char.ToUpperInvariant(result[0]) + result[1..] : result;
    }

    private static async Task CopyAsync(Stream source, Stream target, long count, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (count > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
                if (read == 0)
                    throw new IOException("The book became shorter while it was being sent.");

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static BytesReply Feed(CatalogPage page)
        => new(200, page.MediaType + ";charset=utf-8", page.Body);

    private static async Task WriteAsync(Stream stream, bool headOnly, BytesReply reply, bool keepAlive, CancellationToken cancellationToken)
    {
        var headers = new List<(string, string)>
        {
            ("Content-Type", reply.ContentType),
            ("Content-Length", reply.Body.Length.ToString(CultureInfo.InvariantCulture)),
            ("Cache-Control", reply.CacheControl ?? "no-cache"),
        };
        if (reply.Location is not null)
            headers.Add(("Location", reply.Location));

        await WriteHeadAsync(stream, reply.Status, headers, keepAlive, cancellationToken);
        if (!headOnly && reply.Body.Length > 0)
            await stream.WriteAsync(reply.Body, cancellationToken);
    }

    private static async Task WriteHeadAsync(Stream stream, int status, IEnumerable<(string Name, string Value)> headers, bool keepAlive, CancellationToken cancellationToken)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
        foreach (var (name, value) in headers)
            head.Append(name).Append(": ").Append(value).Append("\r\n");

        head.Append("Server: Consysto-Files\r\n");
        head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), cancellationToken);
    }

    private static string ReasonPhrase(int status)
        => status switch
        {
            200 => "OK",
            206 => "Partial Content",
            302 => "Found",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            416 => "Range Not Satisfiable",
            _ => "Internal Server Error",
        };

    private abstract record Reply
    {
        public static BytesReply NotFound { get; } = Text(404, "Not found");

        public static BytesReply Text(int status, string text)
            => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
    }

    private sealed record BytesReply(int Status, string ContentType, byte[] Body, string? CacheControl = null, string? Location = null) : Reply;

    private sealed record FileReply(CollectionItem Book) : Reply;

    /// <summary>Reads request heads from a connection; bytes after a head stay buffered for the next request.</summary>
    private sealed class RequestReader(Stream stream)
    {
        private readonly byte[] buffer = new byte[MaximumHeaderBytes];
        private int length;

        /// <summary>Null when the client closed the connection between requests.</summary>
        /// <exception cref="InvalidDataException">The head is malformed or too large.</exception>
        public async Task<HttpRequest?> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
                if (end >= 0)
                {
                    var head = Encoding.UTF8.GetString(buffer, 0, end);
                    var consumed = end + 4;
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
                    length -= consumed;
                    return HttpRequest.Parse(head) ?? throw new InvalidDataException("The request line is malformed.");
                }

                if (length == buffer.Length)
                    throw new InvalidDataException("The request head is too large.");

                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
                if (read == 0)
                    return length == 0 ? null : throw new IOException("The connection closed in the middle of a request.");

                length += read;
            }
        }
    }
}
