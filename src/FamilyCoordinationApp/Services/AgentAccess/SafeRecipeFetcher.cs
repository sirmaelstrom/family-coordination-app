using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace FamilyCoordinationApp.Services.AgentAccess;

/// <summary>Why a fetch was refused or failed, safe to show the agent.</summary>
public sealed class RecipeFetchException(string message) : Exception(message);

/// <summary>
/// The outbound fetch behind the agents' recipe import (quest 8bfa42cf, design §6 "outbound fetch"). The browser
/// import's <c>RecipeScraper</c> client follows redirects and checks only the first host's DNS answer; this one:
/// <list type="bullet">
/// <item>accepts https (and http) URLs on the default ports only, with no credentials in the URL;</item>
/// <item>follows at most <see cref="MaxRedirects"/> redirects itself, re-checking every hop;</item>
/// <item>checks the address it ACTUALLY connects to (<see cref="SocketsHttpHandler.ConnectCallback"/>), so a DNS
/// answer that changes between check and connect cannot reach a private, loopback, link-local, metadata, CGNAT,
/// multicast or IPv4-mapped internal address;</item>
/// <item>caps the decompressed body at <see cref="MaxBytes"/> and the whole fetch at <see cref="Timeout"/>;</item>
/// <item>sends no cookies, credentials or app headers.</item>
/// </list>
/// It is stateless and holds one handler for the process (a singleton).
/// </summary>
public sealed class SafeRecipeFetcher : IDisposable
{
    public const int MaxRedirects = 3;
    public const int MaxBytes = 3 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _client;

    public SafeRecipeFetcher() : this(ConnectToPublicAddressAsync) { }

    /// <summary>Test seam: the connect step (production: <see cref="ConnectToPublicAddressAsync"/>).</summary>
    internal SafeRecipeFetcher(Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> connect)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = connect,
        };
        _client = new HttpClient(handler) { Timeout = Timeout, MaxResponseContentBufferSize = MaxBytes };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9,*/*;q=0.5");
    }

    /// <summary>Fetch the page's HTML, or throw <see cref="RecipeFetchException"/> saying why not.</summary>
    public async Task<string> FetchHtmlAsync(string url, CancellationToken ct)
    {
        var current = CheckUrl(url);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (ex.InnerException is RecipeFetchException refused)
            {
                throw refused;
            }
            catch (HttpRequestException)
            {
                throw new RecipeFetchException($"Could not reach {current.Host}.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RecipeFetchException($"{current.Host} took longer than {Timeout.TotalSeconds:0} seconds to answer.");
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects)
                    {
                        throw new RecipeFetchException($"The page redirected more than {MaxRedirects} times.");
                    }
                    current = CheckUrl(new Uri(current, location).ToString());
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new RecipeFetchException($"The site returned HTTP {(int)response.StatusCode}.");
                }
                if (response.Content.Headers.ContentLength is > MaxBytes)
                {
                    throw new RecipeFetchException($"The page is larger than {MaxBytes / (1024 * 1024)} MB.");
                }
                return await ReadCappedAsync(response.Content, ct);
            }
        }
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new RecipeFetchException($"The page is larger than {MaxBytes / (1024 * 1024)} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        var charset = content.Headers.ContentType?.CharSet;
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* unknown charset: UTF-8 */ }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>The URL rules every hop must meet before any connection is made.</summary>
    public static Uri CheckUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new RecipeFetchException("That is not a valid URL.");
        }
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new RecipeFetchException("Only http and https URLs can be imported.");
        }
        if (!uri.IsDefaultPort)
        {
            throw new RecipeFetchException("Only URLs on the standard web ports can be imported.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new RecipeFetchException("URLs with a user name or password cannot be imported.");
        }
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && !IsPublic(IPAddress.Parse(uri.IdnHost.Trim('[', ']'))))
        {
            throw new RecipeFetchException("URLs pointing to internal networks cannot be imported.");
        }
        if (uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            throw new RecipeFetchException("URLs pointing to internal networks cannot be imported.");
        }
        return uri;
    }

    /// <summary>Resolve, keep only public addresses, and connect to one of those. Never a private one.</summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0)
        {
            throw new RecipeFetchException($"Could not resolve {context.DnsEndPoint.Host}.");
        }
        if (addresses.Any(address => !IsPublic(address)))
        {
            throw new RecipeFetchException("URLs pointing to internal networks cannot be imported.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            // Belt and braces: the address the socket really reached.
            if (socket.RemoteEndPoint is IPEndPoint remote && !IsPublic(remote.Address))
            {
                throw new RecipeFetchException("URLs pointing to internal networks cannot be imported.");
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Whether an address is on the public internet (not private, loopback, link-local, CGNAT and so on).</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                   // 0.0.0.0/8 "this network"
                     || b[0] == 10                               // 10/8
                     || b[0] == 127                              // loopback
                     || (b[0] == 100 && b[1] is >= 64 and <= 127) // 100.64/10 CGNAT
                     || (b[0] == 169 && b[1] == 254)             // link-local, cloud metadata
                     || (b[0] == 172 && b[1] is >= 16 and <= 31) // 172.16/12
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)  // 192.0.0/24 protocol assignments
                     || (b[0] == 192 && b[1] == 0 && b[2] == 2)  // TEST-NET-1
                     || (b[0] == 192 && b[1] == 168)             // 192.168/16
                     || (b[0] == 198 && b[1] is 18 or 19)        // benchmarking
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100) // TEST-NET-2
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113)  // TEST-NET-3
                     || b[0] >= 224);                            // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any)) return false;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal) return false;
            var b = address.GetAddressBytes();
            // IPv4-compatible (::a.b.c.d), NAT64 well-known prefix (64:ff9b::/96), 6to4 (2002::/16), Teredo
            // (2001::/32) and documentation (2001:db8::/32) can all smuggle or name a non-public target.
            if (b.Take(12).All(x => x == 0)) return false;
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) return false;
            if (b[0] == 0x20 && b[1] == 0x02) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
            return true;
        }

        return false;
    }

    public void Dispose() => _client.Dispose();
}
