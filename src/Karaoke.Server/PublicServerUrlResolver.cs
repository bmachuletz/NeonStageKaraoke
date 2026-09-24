using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

/// <summary>
/// Produces URLs that are reachable by guests. Loopback and wildcard hosts are
/// replaced with the current LAN address unless an explicit public URL is set.
/// </summary>
public sealed class PublicServerUrlResolver(IOptions<KaraokeOptions> options)
{
    private readonly string? _configured = NormalizeConfigured(options.Value.PublicBaseUrl);

    public string GetBaseUrl(HttpRequest request)
    {
        if (_configured is not null) return _configured;

        var host = request.Host.Host;
        if (!IsLocalOnlyHost(host))
            return $"{request.Scheme}://{request.Host}".TrimEnd('/');

        var advertisedHost = FindLanAddress()?.ToString() ?? host;
        var builder = new UriBuilder(request.Scheme, advertisedHost)
        {
            Port = request.Host.Port ?? request.HttpContext.Connection.LocalPort
        };
        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public string GetEventUrl(HttpRequest request, string inviteToken) =>
        $"{GetBaseUrl(request)}/e/{Uri.EscapeDataString(inviteToken)}";

    private static string? NormalizeConfigured(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "Karaoke:PublicBaseUrl must be an absolute guest-reachable HTTP(S) URL.");
        // localhost/0.0.0.0 sind gültige lokale Servervorgaben, aber keine
        // scanbaren Gästeadressen. In diesem Fall automatisch die LAN-Adresse
        // ermitteln, statt den QR-Endpunkt mit HTTP 500 scheitern zu lassen.
        if (IsLocalOnlyHost(uri.Host)) return null;
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool IsLocalOnlyHost(string host) =>
        string.IsNullOrWhiteSpace(host) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "127.0.0.1" or "::1" or "0.0.0.0" or "::" ||
        IPAddress.TryParse(host, out var address) && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any));

    private static IPAddress? FindLanAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                          adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .SelectMany(adapter =>
        {
            var properties = adapter.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
            return properties.UnicastAddresses.Select(address => new { address.Address, HasGateway = hasGateway });
        })
        .Where(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(candidate.Address) && IsPrivate(candidate.Address))
        .OrderByDescending(candidate => candidate.HasGateway)
        .ThenBy(candidate => candidate.Address.ToString().StartsWith("192.168.", StringComparison.Ordinal) ? 0 : 1)
        .Select(candidate => candidate.Address)
        .FirstOrDefault();

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }
}
