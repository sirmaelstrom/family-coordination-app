using System.Net;
using System.Net.Sockets;
using System.Text;
using FamilyCoordinationApp.Services.AgentAccess;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The agents' outbound recipe fetch (quest 8bfa42cf, design §6): which addresses count as public, which URLs may be
/// fetched at all, and — against a canned local server reached through the connect seam — that every redirect hop
/// is re-checked and the body is capped.
/// </summary>
public sealed class SafeRecipeFetcherTests
{
    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.50.65", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("100.64.0.1", false)]      // CGNAT
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)] // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("64:ff9b::a00:1", false)]   // NAT64 of 10.0.0.1
    [InlineData("2002:a00:1::", false)]     // 6to4 of 10.0.0.1
    public void Only_public_addresses_count_as_public(string address, bool isPublic) =>
        SafeRecipeFetcher.IsPublic(IPAddress.Parse(address)).Should().Be(isPublic);

    [Theory]
    [InlineData("https://www.example.com/recipe")]
    [InlineData("http://example.com/chili")]
    public void Ordinary_web_urls_pass_the_url_check(string url) =>
        SafeRecipeFetcher.CheckUrl(url).Should().NotBeNull();

    [Theory]
    [InlineData("ftp://example.com/recipe")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://example.com:8443/recipe")]
    [InlineData("https://user:pass@example.com/recipe")]
    [InlineData("http://localhost/recipe")]
    [InlineData("http://printer.local/recipe")]
    [InlineData("http://127.0.0.1/recipe")]
    [InlineData("http://[::1]/recipe")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("not a url")]
    public void Unsafe_urls_are_refused_before_any_connection(string url) =>
        FluentActions.Invoking(() => SafeRecipeFetcher.CheckUrl(url)).Should().Throw<RecipeFetchException>();

    [Fact]
    public async Task A_public_page_is_fetched()
    {
        await using var server = new CannedServer(_ => Ok("<html><body>chili</body></html>"));
        using var fetcher = new SafeRecipeFetcher(server.Connect);

        (await fetcher.FetchHtmlAsync("http://recipes.test/chili", CancellationToken.None)).Should().Contain("chili");
    }

    [Fact]
    public async Task A_redirect_to_an_internal_address_is_refused()
    {
        await using var server = new CannedServer(_ =>
            "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var fetcher = new SafeRecipeFetcher(server.Connect);

        var fetch = () => fetcher.FetchHtmlAsync("http://recipes.test/chili", CancellationToken.None);

        await fetch.Should().ThrowAsync<RecipeFetchException>().WithMessage("*internal networks*");
        server.Requests.Should().Be(1, "the internal hop is refused before any connection to it");
    }

    [Fact]
    public async Task A_redirect_chain_longer_than_the_limit_is_refused()
    {
        await using var server = new CannedServer(n =>
            $"HTTP/1.1 302 Found\r\nLocation: http://recipes.test/hop{n + 1}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var fetcher = new SafeRecipeFetcher(server.Connect);

        var fetch = () => fetcher.FetchHtmlAsync("http://recipes.test/hop0", CancellationToken.None);

        await fetch.Should().ThrowAsync<RecipeFetchException>().WithMessage("*redirected more than*");
        server.Requests.Should().Be(SafeRecipeFetcher.MaxRedirects + 1);
    }

    [Fact]
    public async Task An_oversized_page_is_refused()
    {
        var big = new string('x', SafeRecipeFetcher.MaxBytes + 1);
        await using var server = new CannedServer(_ => Ok(big));
        using var fetcher = new SafeRecipeFetcher(server.Connect);

        var fetch = () => fetcher.FetchHtmlAsync("http://recipes.test/huge", CancellationToken.None);

        await fetch.Should().ThrowAsync<RecipeFetchException>().WithMessage("*larger than*");
    }

    [Fact]
    public async Task An_error_status_is_reported_by_its_code()
    {
        await using var server = new CannedServer(_ => "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var fetcher = new SafeRecipeFetcher(server.Connect);

        var fetch = () => fetcher.FetchHtmlAsync("http://recipes.test/chili", CancellationToken.None);

        await fetch.Should().ThrowAsync<RecipeFetchException>().WithMessage("*HTTP 403*");
    }

    private static string Ok(string body) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    /// <summary>A loopback server answering the n-th connection with <c>respond(n)</c>. The seam routes every host to it.</summary>
    private sealed class CannedServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public CannedServer(Func<int, string> respond)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (OperationCanceledException) { return; }
                    var n = Interlocked.Increment(ref _requests) - 1;
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[8192];
                        await stream.ReadExactlyAsync(buffer);
                        var bytes = Encoding.UTF8.GetBytes(respond(n));
                        try { await stream.WriteAsync(bytes); } catch (IOException) { }
                    }
                }
            });
        }

        public async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch (OperationCanceledException) { }
        }
    }
}
