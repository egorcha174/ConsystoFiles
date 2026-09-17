using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Consysto.BookPreview.Opds;

/// <summary>Reads OPDS catalog pages over HTTP (or from local files, for tests) and downloads books.</summary>
public sealed class OpdsClient : IDisposable
{
    private const int MaximumPageBytes = 16 * 1024 * 1024;
    private const long MaximumBookBytes = 1024L * 1024 * 1024;
    private const string FeedAccept = "application/opds+json, application/atom+xml;profile=opds-catalog;q=0.9, application/atom+xml;q=0.8, application/json;q=0.7, */*;q=0.5";

    private readonly HttpClient http;

    public OpdsClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ConsystoFiles/1.0 (OPDS catalog client)");
    }

    /// <summary>Sent as HTTP Basic authentication when set.</summary>
    public NetworkCredential? Credentials { get; set; }

    public async Task<OpdsFeed> GetFeedAsync(Uri address, CancellationToken cancellationToken = default)
    {
        var (data, finalAddress) = await GetBytesAsync(address, FeedAccept, cancellationToken);
        return Parse(data, finalAddress);
    }

    /// <summary>The page's search template, fetching the OpenSearch description when the page only links to one.</summary>
    public async Task<string?> GetSearchTemplateAsync(OpdsFeed feed, CancellationToken cancellationToken = default)
    {
        if (feed.SearchTemplate is not null || feed.SearchDescription is null)
            return feed.SearchTemplate;

        var (data, finalAddress) = await GetBytesAsync(feed.SearchDescription, "application/opensearchdescription+xml, application/xml;q=0.9, */*;q=0.5", cancellationToken);
        return feed.SearchTemplate = OpdsSearch.ParseDescription(LoadXml(data), finalAddress);
    }

    public static Uri BuildSearchAddress(string template, string terms)
        => OpdsSearch.BuildAddress(template, terms);

    public static OpdsFeed Parse(byte[] data, Uri address)
    {
        var start = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            start = 3;

        while (start < data.Length && data[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            start++;

        if (start >= data.Length)
            throw new InvalidDataException("The catalog page is empty.");

        if (data[start] == (byte)'{')
        {
            using var json = JsonDocument.Parse(data.AsMemory(start));
            return OpdsJsonParser.Parse(json, address);
        }

        if (data[start] != (byte)'<')
            throw new InvalidDataException("The address does not point to an OPDS catalog.");

        var document = LoadXml(data);
        if (document.Root?.Name.LocalName.Equals("html", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("The address opened a web page, not an OPDS catalog.");

        return OpdsAtomParser.Parse(document, address);
    }

    /// <summary>
    /// Downloads a book into <paramref name="directory"/> as "<paramref name="baseName"/>.ext", choosing the extension from the
    /// server's file name, then the link's format; the file only appears under its final name once it is complete.
    /// </summary>
    public async Task<string> DownloadAsync(OpdsAcquisition acquisition, string directory, string baseName, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var accept = acquisition.MediaType.Length > 0 ? $"{acquisition.MediaType}, */*;q=0.5" : "*/*";
        using var response = await SendAsync(acquisition.Address, accept, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var servedType = response.Content.Headers.ContentType?.MediaType;
        if (servedType is "text/html" && acquisition.Format.Name != "HTML")
            throw new InvalidDataException("The catalog returned a web page instead of the book (a sign-in or limit page).");

        var disposition = response.Content.Headers.ContentDisposition;
        var serverName = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"');
        var extension = OpdsBookFormat.FromFileName(serverName)?.Extension
            ?? NonEmpty(acquisition.Format.Extension)
            ?? NonEmpty(OpdsBookFormat.FromMediaType(servedType, response.RequestMessage?.RequestUri).Extension)
            ?? NonEmpty(Path.GetExtension(serverName))
            ?? string.Empty;

        Directory.CreateDirectory(directory);
        var target = UniquePath(directory, SafeFileName(baseName), extension);
        var partial = target + ".part";
        var total = response.Content.Headers.ContentLength;
        var completed = false;

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (received > MaximumBookBytes)
                        throw new InvalidDataException("The book is larger than 1 GB.");

                    if (total > 0)
                        progress?.Report((double)received / total.Value);
                }
            }

            File.Move(partial, target);
            completed = true;
            progress?.Report(1);
            return target;
        }
        finally
        {
            if (!completed)
            {
                try { File.Delete(partial); }
                catch (IOException) { }
            }
        }
    }

    public void Dispose()
        => http.Dispose();

    private async Task<(byte[] Data, Uri Address)> GetBytesAsync(Uri address, string accept, CancellationToken cancellationToken)
    {
        if (address.IsFile)
            return (await File.ReadAllBytesAsync(address.LocalPath, cancellationToken), address);

        using var response = await SendAsync(address, accept, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength > MaximumPageBytes)
            throw new InvalidDataException("The catalog page is larger than 16 MB.");

        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (data, response.RequestMessage?.RequestUri ?? address);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri address, string accept, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        if (Credentials is { } credentials)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}")));

        var response = await http.SendAsync(request, completion, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var realm = response.Headers.WwwAuthenticate.FirstOrDefault()?.Parameter;
            response.Dispose();
            throw new OpdsAuthenticationRequiredException(address, realm);
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"The catalog answered {(int)status} {status}.", null, status);
        }

        return response;
    }

    private static XDocument LoadXml(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = false,
        });
        return XDocument.Load(reader);
    }

    private static string? NonEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length > 150)
            safe = safe[..150].TrimEnd();

        return safe.Length > 0 ? safe : "book";
    }

    private static string UniquePath(string directory, string name, string extension)
    {
        var path = Path.Combine(directory, name + extension);
        for (var copy = 2; File.Exists(path) || File.Exists(path + ".part"); copy++)
            path = Path.Combine(directory, $"{name} ({copy}){extension}");

        return path;
    }
}
