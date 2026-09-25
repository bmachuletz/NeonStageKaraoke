using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class QobuzDownloadService(QobuzPluginSettingsService settings)
{
    public async Task<string> DownloadAsync(SpotifyTrackDto track, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
            { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        var configuration = await settings.GetWorkerSettingsAsync(cancellationToken);
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.AppId) ||
            string.IsNullOrWhiteSpace(configuration.AppSecret) ||
            string.IsNullOrWhiteSpace(configuration.UserAuthToken))
            throw new InvalidOperationException("Qobuz ist nicht vollständig für Kaufdownloads konfiguriert.");
        var trackId = track.QobuzId?.Trim();
        if (string.IsNullOrWhiteSpace(trackId) || trackId.Any(character => !char.IsAsciiDigit(character)))
            throw new InvalidDataException("Der Qobuz-Wunsch enthält keine gültige Track-ID.");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signatureText = $"trackgetFileUrlformat_id{configuration.FormatId}intentdownload" +
                            $"track_id{trackId}{timestamp}{configuration.AppSecret}";
        var signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(signatureText))).ToLowerInvariant();
        var endpoint = configuration.ApiBaseUrl.TrimEnd('/') + "/track/getFileUrl";
        using var queryContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["app_id"] = configuration.AppId, ["format_id"] = configuration.FormatId.ToString(),
            ["intent"] = "download", ["request_ts"] = timestamp, ["request_sig"] = signature,
            ["track_id"] = trackId
        });
        var query = await queryContent.ReadAsStringAsync(cancellationToken);
        var uri = new Uri(endpoint + "?" + query);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-App-Id", configuration.AppId);
        request.Headers.TryAddWithoutValidation("X-User-Auth-Token", configuration.UserAuthToken);
        request.Headers.UserAgent.ParseAdd("NeonStageKaraoke/1.0");
        using var apiResponse = await SendCheckedAsync(client, request, requireHttps: false, cancellationToken);
        apiResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await apiResponse.Content.ReadAsStringAsync(cancellationToken));
        var downloadUrl = document.RootElement.TryGetProperty("url", out var urlValue) &&
                          urlValue.ValueKind == JsonValueKind.String ? urlValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("Qobuz hat keinen autorisierten Kaufdownload bereitgestellt.");
        var downloadUri = new Uri(downloadUrl);
        await ValidatePublicUriAsync(downloadUri, requireHttps: true, cancellationToken);
        Directory.CreateDirectory(destinationDirectory);
        var extension = configuration.FormatId == 5 ? ".mp3" : ".flac";
        var destination = Path.Combine(destinationDirectory, "download" + extension);
        using var audioRequest = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        audioRequest.Headers.UserAgent.ParseAdd("NeonStageKaraoke/1.0");
        using var audioResponse = await SendCheckedAsync(client, audioRequest, requireHttps: true, cancellationToken);
        audioResponse.EnsureSuccessStatusCode();
        await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await audioResponse.Content.CopyToAsync(target, cancellationToken);
        if (new FileInfo(destination).Length < 64 * 1024)
            throw new InvalidDataException("Qobuz hat eine unplausibel kleine Audiodatei geliefert.");
        if (extension == ".flac")
        {
            var magic = new byte[4];
            await using var source = File.OpenRead(destination);
            if (await source.ReadAsync(magic, cancellationToken) != 4 || Encoding.ASCII.GetString(magic) != "fLaC")
                throw new InvalidDataException("Qobuz hat keine gültige FLAC-Datei geliefert.");
        }
        return destination;
    }

    private static async Task<HttpResponseMessage> SendCheckedAsync(HttpClient client,
        HttpRequestMessage initial, bool requireHttps, CancellationToken cancellationToken)
    {
        var request = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            await ValidatePublicUriAsync(request.RequestUri!, requireHttps, cancellationToken);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is null) return response;
            var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(request.RequestUri!, response.Headers.Location);
            response.Dispose();
            request = new HttpRequestMessage(HttpMethod.Get, next);
            // Never forward account credentials to a different redirect host.
            if (string.Equals(next.Scheme, initial.RequestUri!.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(next.Host, initial.RequestUri.Host, StringComparison.OrdinalIgnoreCase) &&
                next.Port == initial.RequestUri.Port)
                foreach (var header in initial.Headers)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            else
                request.Headers.UserAgent.ParseAdd("NeonStageKaraoke/1.0");
        }
        throw new InvalidOperationException("Qobuz hat zu viele Weiterleitungen geliefert.");
    }

    private static async Task ValidatePublicUriAsync(Uri uri, bool requireHttps,
        CancellationToken cancellationToken)
    {
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        if (!uri.IsAbsoluteUri || requireHttps && !isHttps || !requireHttps && !isHttp && !isHttps)
            throw new InvalidDataException("Qobuz hat eine unzulässige Downloadadresse geliefert.");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsPrivate))
            throw new InvalidDataException("Die Qobuz-Adresse verweist nicht auf einen öffentlichen Server.");
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is 0 or 10 or 127 || bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] >= 224;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
               address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) ||
               (address.GetAddressBytes()[0] & 0xfe) == 0xfc;
    }
}
