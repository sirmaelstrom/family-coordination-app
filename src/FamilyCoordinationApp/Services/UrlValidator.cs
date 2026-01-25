using System.Net;
using System.Net.Sockets;

namespace FamilyCoordinationApp.Services;

public interface IUrlValidator
{
    /// <summary>
    /// Validates URL is safe to fetch (not localhost, private IP, or non-http(s) scheme).
    /// </summary>
    bool IsUrlSafe(string url);

    /// <summary>
    /// Validates URL and returns error message if invalid.
    /// </summary>
    (bool IsValid, string? ErrorMessage) ValidateUrl(string url);
}

public class UrlValidator : IUrlValidator
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https" };

    public bool IsUrlSafe(string url)
    {
        return ValidateUrl(url).IsValid;
    }

    public (bool IsValid, string? ErrorMessage) ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, "URL cannot be empty");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, "Invalid URL format");

        // Only allow http/https
        if (!AllowedSchemes.Contains(uri.Scheme))
            return (false, $"URL scheme '{uri.Scheme}' is not allowed. Use http or https.");

        // Resolve DNS to check IP addresses
        try
        {
            var addresses = Dns.GetHostAddresses(uri.Host);
            foreach (var addr in addresses)
            {
                if (IsPrivateOrLocalAddress(addr))
                    return (false, "URLs pointing to internal networks are not allowed");
            }
        }
        catch (SocketException)
        {
            return (false, "Could not resolve hostname");
        }

        return (true, null);
    }

    private static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        // Loopback (127.0.0.0/8, ::1)
        if (IPAddress.IsLoopback(address))
            return true;

        // 0.0.0.0 or ::
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        byte[] bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            // 169.254.0.0/16 (link-local, includes AWS metadata endpoint)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
        }

        return false;
    }
}
