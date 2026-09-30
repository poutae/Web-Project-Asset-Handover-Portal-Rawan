using System.Net;
using System.Net.Sockets;

namespace Portal.Infrastructure.Deployments;

/// <summary>
/// Decides which repositories the platform will fetch. Only HTTPS repositories on public hosts are
/// accepted (plus local ones when explicitly enabled for development), which keeps the portal from being
/// pointed at its own network. Credentials never live in the URL.
/// </summary>
public static class RepositoryUrlPolicy
{
    public const int MaxLength = 500;

    /// <summary>Returns an error message, or null if the URL is acceptable. Resolves DNS for HTTPS hosts.</summary>
    public static async Task<string?> ValidateAsync(string? url, bool allowLocal, CancellationToken ct)
    {
        var syntax = ValidateSyntax(url, allowLocal);
        if (syntax is not null || url is null)
        {
            return syntax;
        }

        var uri = new Uri(url);
        if (uri.IsFile)
        {
            return null;
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal))
        {
            return IsPublic(literal) ? null : "That address is not reachable from the build service.";
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
            if (addresses.Length == 0)
            {
                return "The repository host could not be resolved.";
            }

            return addresses.All(IsPublic) ? null : "That host resolves to a private address.";
        }
        catch (SocketException)
        {
            return "The repository host could not be resolved.";
        }
    }

    /// <summary>Checks the shape of the URL without touching the network.</summary>
    public static string? ValidateSyntax(string? url, bool allowLocal)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength)
        {
            return $"Enter a repository URL of at most {MaxLength} characters.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || url.Any(char.IsControl) || url.Contains(' ', StringComparison.Ordinal))
        {
            return "The repository URL is not valid.";
        }

        if (uri.IsFile)
        {
            return allowLocal ? null : "Only https:// repositories are supported.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "Only https:// repositories are supported.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "Do not put credentials in the URL; use the access token field.";
        }

        var host = uri.DnsSafeHost.ToLowerInvariant();
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal) && !IsPublic(literal))
        {
            return "That address is not reachable from the build service.";
        }

        if (host is "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || !host.Contains('.', StringComparison.Ordinal) && !IPAddress.TryParse(host.Trim('[', ']'), out _))
        {
            return "That host is not reachable from the build service.";
        }

        return null;
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var first = address.GetAddressBytes()[0];
            return (first & 0xFE) != 0xFC; // fc00::/7 unique local
        }

        var b = address.GetAddressBytes();
        return !(b[0] == 10
            || b[0] == 127
            || b[0] == 0
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || b[0] >= 224);
    }
}
