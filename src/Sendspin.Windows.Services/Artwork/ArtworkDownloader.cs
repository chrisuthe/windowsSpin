using System.Net;
using Microsoft.Extensions.Logging;

namespace Sendspin.Windows.Services.Artwork;

/// <summary>
/// Downloads album artwork from a URL the server supplied, within limits a misbehaving server or
/// an unintended redirect can't exceed.
/// </summary>
/// <remarks>
/// <para>
/// Redirects are followed here, one hop at a time, so every target passes <see cref="TryValidate"/>
/// before it is requested. The handler's own redirect following would request a target first and
/// leave nothing to check but the final URL. The client must be built with
/// <c>AllowAutoRedirect = false</c>; a redirect the handler followed anyway is still refused once it
/// is seen.
/// </para>
/// <para>
/// The body is capped at <see cref="MaxBytes"/>: refused up front from its Content-Length, and
/// while reading when there isn't one. The client's default would buffer up to 2 GB.
/// </para>
/// </remarks>
public sealed class ArtworkDownloader
{
    /// <summary>Largest artwork body accepted.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>Most redirects followed for one download.</summary>
    public const int MaxRedirects = 5;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkDownloader"/> class.
    /// </summary>
    /// <param name="httpClient">A client whose handler does not follow redirects itself.</param>
    /// <param name="logger">Logger for refused downloads.</param>
    public ArtworkDownloader(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Validates that an artwork URL is safe to fetch: absolute HTTP or HTTPS, and not a loopback
    /// host, so a server can't point the app at local services.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <param name="uri">The parsed URI if valid.</param>
    /// <returns>True if the URL is safe to fetch.</returns>
    public static bool TryValidate(string url, out Uri? uri)
    {
        uri = null;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            return false;

        // Only allow HTTP/HTTPS schemes
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return false;

        // Block localhost and loopback addresses to prevent accessing local services
        var host = uri.Host.ToLowerInvariant();
        if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]")
            return false;

        // Check for loopback IP addresses
        if (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip))
            return false;

        return true;
    }

    /// <summary>
    /// Downloads the image at <paramref name="url"/>.
    /// </summary>
    /// <param name="url">The artwork URL from the server.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The image bytes, or null when the URL, a redirect or the size was refused (logged).</returns>
    /// <exception cref="HttpRequestException">The request failed or the server returned an error status.</exception>
    public async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!TryValidate(url, out var uri))
        {
            _logger.LogWarning("Blocked artwork fetch from invalid or unsafe URL: {Url}", url);
            return null;
        }

        for (var redirects = 0; ; redirects++)
        {
            using var response = await _httpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (response.Headers.Location is not { } location || redirects == MaxRedirects)
                {
                    _logger.LogWarning("Artwork redirect from {Url} not followed (no target, or more than {Max} redirects)", uri, MaxRedirects);
                    return null;
                }

                var target = location.IsAbsoluteUri ? location : new Uri(uri!, location);
                if (!TryValidate(target.AbsoluteUri, out uri))
                {
                    _logger.LogWarning("Blocked artwork redirect to invalid or unsafe URL: {Url}", target);
                    return null;
                }

                continue;
            }

            response.EnsureSuccessStatusCode();

            // Only reachable if the handler followed a redirect itself; refuse what it landed on.
            if (response.RequestMessage?.RequestUri is { } landed && !TryValidate(landed.AbsoluteUri, out _))
            {
                _logger.LogWarning("Blocked artwork from invalid or unsafe URL reached by redirect: {Url}", landed);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                _logger.LogWarning(
                    "Artwork from {Url} refused: {Length} bytes is over the {Max}-byte limit",
                    uri,
                    response.Content.Headers.ContentLength,
                    MaxBytes);
                return null;
            }

            try
            {
                // Enforces the cap while reading, for a body sent without a Content-Length.
                await response.Content.LoadIntoBufferAsync(MaxBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Artwork from {Url} refused: body is over the {Max}-byte limit", uri, MaxBytes);
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
