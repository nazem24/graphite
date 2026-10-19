using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Graphite.Core.Canvas;

/// <summary>
/// A small, read-only Canvas LMS client: GET requests only, with a personal access token.
///
/// What it takes care of, learned the hard way with HSG's Canvas:
/// <list type="bullet">
/// <item>Canvas refuses requests without a normal browser-like User-Agent (it answers 403), so one is always sent.</item>
/// <item>Lists come in pages of 10 unless <c>per_page=100</c> is asked for; the next page is in the <c>Link</c> header.</item>
/// <item>403 with a rate-limit message and 429 mean "slow down": wait and try again.</item>
/// <item>File downloads redirect to other hosts. Redirects are followed by hand so the token is only ever sent to
/// the Canvas host itself, never to a file server.</item>
/// </list>
/// </summary>
public sealed class CanvasClient : IDisposable
{
    public const string DefaultBaseUrl = "https://learning.unisg.ch";

    /// <summary>A desktop browser's User-Agent with Graphite added at the end.</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Graphite";

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly bool _ownsHttp;

    public Uri BaseUri { get; }

    /// <param name="handler">For tests; the default handler never follows redirects by itself.</param>
    public CanvasClient(string baseUrl, string token, HttpMessageHandler? handler = null)
    {
        BaseUri = NormalizeBase(baseUrl);
        _token = token.Trim();
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _ownsHttp = true;
    }

    /// <summary>"learning.unisg.ch/" → https://learning.unisg.ch</summary>
    public static Uri NormalizeBase(string baseUrl)
    {
        string b = (baseUrl ?? "").Trim().TrimEnd('/');
        if (b.Length == 0) b = DefaultBaseUrl;
        if (!b.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !b.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            b = "https://" + b;
        if (!Uri.TryCreate(b, UriKind.Absolute, out var uri)) throw new ArgumentException("That is not a web address.", nameof(baseUrl));
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    public string BaseUrl => BaseUri.AbsoluteUri.TrimEnd('/');

    /// <summary>An API path ("/courses") becomes https://host/api/v1/courses; a full URL stays as it is.</summary>
    public Uri ApiUri(string pathOrUrl) =>
        Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp)
            ? abs
            : new Uri(BaseUrl + "/api/v1" + (pathOrUrl.StartsWith('/') ? "" : "/") + pathOrUrl);

    public bool IsCanvasHost(Uri uri) => string.Equals(uri.Host, BaseUri.Host, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------- requests

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(40);

    /// <summary>GET with retries for rate limiting and redirects followed by hand. The caller owns the response.
    /// <paramref name="timeout"/> covers the headers only (a download streams afterwards).</summary>
    public async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken ct, TimeSpan? timeout = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            var response = await SendFollowingRedirectsAsync(url, ct, timeout ?? RequestTimeout).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if ((code == 429 || code == 403) && attempt < 3)
            {
                string body = code == 403 ? await SafeBodyAsync(response, ct).ConfigureAwait(false) : "";
                if (code == 429 || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(900 * (attempt + 1)), ct).ConfigureAwait(false);
                    continue;
                }
                // a real "forbidden": hand the answer back (its body has been read; make it readable again)
                var replay = new HttpResponseMessage(response.StatusCode)
                {
                    Content = new StringContent(body),
                    RequestMessage = response.RequestMessage,
                };
                response.Dispose();
                return replay;
            }
            return response;
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri url, CancellationToken ct, TimeSpan timeout)
    {
        var current = url;
        for (int hop = 0; hop < 8; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, */*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,de;q=0.8");
            // The token is a full-access credential: only the Canvas host ever sees it.
            if (_token.Length > 0 && IsCanvasHost(current) && current.Scheme == Uri.UriSchemeHttps)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new HttpRequestException("Canvas did not answer in time.");
            }

            int code = (int)response.StatusCode;
            if ((code is 301 or 302 or 303 or 307 or 308) && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                continue;
            }
            return response;
        }
        throw new HttpRequestException("Too many redirects.");
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ""; }
    }

    private static async Task<CanvasApiException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await SafeBodyAsync(response, ct).ConfigureAwait(false);
        string message = CanvasParser.ErrorMessage(body) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
        return new CanvasApiException((int)response.StatusCode, message);
    }

    /// <summary>One JSON answer (an object, or a single page of a list).</summary>
    public async Task<JsonElement> GetJsonAsync(string pathOrUrl, CancellationToken ct = default)
    {
        using var response = await SendAsync(ApiUri(pathOrUrl), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private static readonly Regex NextLink = new(@"<([^>]+)>\s*;\s*rel=""?next""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The URL of the next page in a Link header, if there is one.</summary>
    public static string? NextPage(string? linkHeader)
    {
        if (string.IsNullOrEmpty(linkHeader)) return null;
        var m = NextLink.Match(linkHeader);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Every page of a list. Remember to ask for <c>per_page=100</c> in the path.</summary>
    public async Task<List<JsonElement>> GetAllAsync(string path, CancellationToken ct = default)
    {
        var all = new List<JsonElement>();
        Uri? url = ApiUri(path);
        for (int page = 0; url != null && page < 60; page++)
        {
            using var response = await SendAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct).ConfigureAwait(false);
            await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    all.Add(doc.RootElement.Clone());
                    return all;
                }
                foreach (var el in doc.RootElement.EnumerateArray()) all.Add(el.Clone());
            }

            string? link = response.Headers.TryGetValues("Link", out var values) ? string.Join(", ", values) : null;
            string? next = NextPage(link);
            url = next != null && Uri.TryCreate(next, UriKind.Absolute, out var n) && IsCanvasHost(n) ? n : null;
        }
        return all;
    }

    /// <summary>Stream a file to <paramref name="destination"/>. The URL is a Canvas download link (it may redirect
    /// to a file host, which never gets the token). <paramref name="progress"/> reports bytes written.</summary>
    public async Task DownloadAsync(string url, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) uri = new Uri(BaseUri, url);
        using var response = await SendAsync(uri, ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, ct).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            progress?.Report(total);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
