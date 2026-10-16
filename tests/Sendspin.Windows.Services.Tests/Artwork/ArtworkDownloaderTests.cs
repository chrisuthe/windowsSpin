// <copyright file="ArtworkDownloaderTests.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.Windows.Services.Artwork;
using Xunit;

namespace Sendspin.Windows.Services.Tests.Artwork;

public class ArtworkDownloaderTests
{
    private static readonly byte[] Image = [1, 2, 3, 4];

    [Fact]
    public async Task DownloadsTheImage()
    {
        var handler = new StubHandler(_ => Ok(Image));

        Assert.Equal(Image, await Download(handler, "http://ma.local/art.jpg"));
    }

    [Fact]
    public async Task ARedirectToLoopback_IsNeverRequested()
    {
        // The server's URL passes validation; the redirect target must be checked too, before
        // any request is made to it.
        var handler = new StubHandler(r => r.RequestUri!.Host == "ma.local"
            ? Redirect("http://127.0.0.1:8080/admin")
            : Ok(Image));

        Assert.Null(await Download(handler, "http://ma.local/art.jpg"));
        Assert.DoesNotContain(handler.Requested, u => u.Host == "127.0.0.1");
    }

    [Fact]
    public async Task ARedirectToAnotherHost_IsFollowed()
    {
        // Cover Art Archive answers with a redirect to archive.org, so redirects can't simply be refused.
        var handler = new StubHandler(r => r.RequestUri!.Host == "coverartarchive.org"
            ? Redirect("https://ia800.us.archive.org/cover.jpg")
            : Ok(Image));

        Assert.Equal(Image, await Download(handler, "https://coverartarchive.org/release/x/front"));
    }

    [Fact]
    public async Task ARelativeRedirect_IsResolvedAndChecked()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/old"
            ? Redirect("/new", relative: true)
            : Ok(Image));

        Assert.Equal(Image, await Download(handler, "http://ma.local/old"));
        Assert.Equal("http://ma.local/new", handler.Requested[^1].AbsoluteUri);
    }

    [Fact]
    public async Task ARedirectLoop_Ends()
    {
        var handler = new StubHandler(_ => Redirect("http://ma.local/again"));

        Assert.Null(await Download(handler, "http://ma.local/again"));
        Assert.Equal(ArtworkDownloader.MaxRedirects + 1, handler.Requested.Count);
    }

    [Fact]
    public async Task AnOversizedBody_IsRefused_FromItsContentLength()
    {
        var handler = new StubHandler(_ => Ok(new byte[ArtworkDownloader.MaxBytes + 1]));

        Assert.Null(await Download(handler, "http://ma.local/art.jpg"));
    }

    [Fact]
    public async Task AnOversizedBody_IsRefused_WithoutAContentLength()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnsizedContent(ArtworkDownloader.MaxBytes + 1),
        });

        Assert.Null(await Download(handler, "http://ma.local/art.jpg"));
    }

    [Theory]
    [InlineData("http://ma.local/art.jpg", true)]
    [InlineData("https://coverartarchive.org/x", true)]
    [InlineData("http://localhost/art.jpg", false)]
    [InlineData("http://127.0.0.1/art.jpg", false)]
    [InlineData("http://127.5.5.5/art.jpg", false)]
    [InlineData("http://[::1]/art.jpg", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("not a url", false)]
    public void TheUrlRules(string url, bool allowed)
    {
        Assert.Equal(allowed, ArtworkDownloader.TryValidate(url, out _));
    }

    private static Task<byte[]?> Download(StubHandler handler, string url)
    {
        using var client = new HttpClient(handler);
        return new ArtworkDownloader(client, NullLogger.Instance).DownloadAsync(url);
    }

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string location, bool relative = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, relative ? UriKind.Relative : UriKind.Absolute);
        return response;
    }

    /// <summary>Answers every request from a function and records what was requested.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>A body with no Content-Length, as a chunked response would have.</summary>
    private sealed class UnsizedContent(int length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[length]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
