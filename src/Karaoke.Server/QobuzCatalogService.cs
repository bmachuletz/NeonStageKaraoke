using System.Globalization;
using System.Text.Json;
using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class QobuzCatalogService(IHttpClientFactory clients, QobuzPluginSettingsService settings,
    LyricsAvailabilityService lyricsAvailability)
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        var status = await settings.GetAsync(cancellationToken);
        return status.Enabled && status.Configured;
    }

    public async Task<IReadOnlyList<SpotifyTrackDto>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var configuration = await settings.GetWorkerSettingsAsync(cancellationToken);
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.AppId) ||
            string.IsNullOrWhiteSpace(configuration.UserAuthToken)) return [];

        var url = $"{configuration.ApiBaseUrl.TrimEnd('/')}/track/search?app_id={Uri.EscapeDataString(configuration.AppId)}" +
                  $"&query={Uri.EscapeDataString(query)}&limit=10";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-App-Id", configuration.AppId);
        request.Headers.TryAddWithoutValidation("X-User-Auth-Token", configuration.UserAuthToken);
        request.Headers.UserAgent.ParseAdd("NeonStageKaraoke/1.0");
        using var response = await clients.CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var items = GetItems(document.RootElement);
        var mapped = items.Select(Map).Where(track => track is not null).Cast<SpotifyTrackDto>().ToArray();
        return await Task.WhenAll(mapped.Select(track => lyricsAvailability.CheckAsync(track, cancellationToken)));
    }

    private static IEnumerable<JsonElement> GetItems(JsonElement root)
    {
        if (root.TryGetProperty("tracks", out var tracks) && tracks.TryGetProperty("items", out var trackItems) &&
            trackItems.ValueKind == JsonValueKind.Array) return trackItems.EnumerateArray().ToArray();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.EnumerateArray().ToArray();
        return [];
    }

    internal static SpotifyTrackDto? Map(JsonElement item)
    {
        var id = Text(item, "id");
        var title = Text(item, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) return null;
        var albumElement = Object(item, "album");
        var performer = Text(Object(item, "performer"), "name");
        var artist = performer ?? Text(Object(albumElement, "artist"), "name") ?? string.Empty;
        var album = Text(albumElement, "title") ?? string.Empty;
        var image = Text(Object(albumElement, "image"), "large") ??
                    Text(Object(albumElement, "image"), "small") ??
                    Text(Object(albumElement, "image"), "thumbnail");
        var duration = Integer(item, "duration") is { } seconds ? checked(seconds * 1000) : 0;
        var price = Decimal(item, "price") ?? Decimal(item, "download_price") ??
                    Decimal(Object(item, "purchase"), "price") ?? Decimal(albumElement, "price");
        var currency = Text(item, "currency") ?? Text(item, "currency_code") ??
                       Text(Object(item, "purchase"), "currency") ?? Text(albumElement, "currency");
        var bitDepth = Integer(item, "maximum_bit_depth") ?? Integer(albumElement, "maximum_bit_depth");
        var sampleRate = Decimal(item, "maximum_sampling_rate") ?? Decimal(albumElement, "maximum_sampling_rate");
        var quality = bitDepth is null ? null : sampleRate is null
            ? $"FLAC {bitDepth} bit"
            : $"FLAC {bitDepth} bit / {sampleRate:0.#} kHz";
        var sourceUrl = Text(item, "url") ?? Text(item, "share_url") ?? $"https://open.qobuz.com/track/{id}";
        var previewUrl = ValidHttpUrl(Text(item, "preview_url")) ?? ValidHttpUrl(Text(item, "sample_url"));
        return new($"qobuz:{id}", $"qobuz:track:{id}", title, artist, album, image, duration, false,
            null, AudioCatalogSource.Qobuz, sourceUrl, price, currency, id, quality,
            AudioDownloadSource.Qobuz, previewUrl);
    }

    private static string? ValidHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;

    private static JsonElement Object(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Object ? value : default;

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? Integer(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var result) ? result : null;

    private static decimal? Decimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out number) ? number : null;
    }
}
