using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Alostora.RestHouse;

public sealed record MetadataLookupResult(
    string Title, string Description, int? Year, string Genre, string Kind,
    string? PosterUrl, string? Director, double? Rating, int? RuntimeMinutes,
    string? ReleaseDate, string Provider);

public sealed class MetadataLookupService
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ALOSTORA-REST-HOUSE/1.0");
        return client;
    }

    public async Task<MetadataLookupResult?> SearchAsync(string query, string kind, CancellationToken cancellationToken = default)
    {
        query = (query ?? "").Trim();
        if (query.Length is < 2 or > 160) throw new ArgumentException("اكتب عنواناً من حرفين إلى 160 حرفاً.");
        return string.Equals(kind, "Series", StringComparison.OrdinalIgnoreCase)
            ? await SearchSeriesAsync(query, cancellationToken)
            : await SearchMovieAsync(query, cancellationToken);
    }

    private static async Task<MetadataLookupResult?> SearchSeriesAsync(string query, CancellationToken token)
    {
        var url = "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(query);
        using var response = await Client.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        foreach (var hit in json.RootElement.EnumerateArray())
        {
            if (!hit.TryGetProperty("show", out var show)) continue;
            var title = Text(show, "name");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var premiered = Text(show, "premiered");
            var year = int.TryParse(premiered?.Length >= 4 ? premiered[..4] : null, out var y) ? y : (int?)null;
            var summary = StripHtml(Text(show, "summary"));
            var genres = show.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array
                ? string.Join("، ", gs.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)))
                : "";
            var poster = show.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
                ? Text(image, "original") ?? Text(image, "medium") : null;
            double? rating = show.TryGetProperty("rating", out var ratingObj) && ratingObj.TryGetProperty("average", out var avg) && avg.ValueKind == JsonValueKind.Number
                ? avg.GetDouble() : null;
            int? runtime = show.TryGetProperty("averageRuntime", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : null;
            return new(title, summary ?? "", year, genres, "Series", poster, null, rating, runtime, premiered, "TVmaze");
        }
        return null;
    }

    private static async Task<MetadataLookupResult?> SearchMovieAsync(string query, CancellationToken token)
    {
        var url = "https://itunes.apple.com/search?media=movie&entity=movie&limit=5&term=" + Uri.EscapeDataString(query);
        using var response = await Client.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (!json.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        foreach (var movie in results.EnumerateArray())
        {
            var title = Text(movie, "trackName");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var released = Text(movie, "releaseDate");
            var year = int.TryParse(released?.Length >= 4 ? released[..4] : null, out var y) ? y : (int?)null;
            var poster = Text(movie, "artworkUrl600") ?? Text(movie, "artworkUrl100");
            var runtime = movie.TryGetProperty("trackTimeMillis", out var ms) && ms.ValueKind == JsonValueKind.Number
                ? (int?)Math.Max(1, (int)Math.Round(ms.GetDouble() / 60000d)) : null;
            return new(title, Text(movie, "longDescription") ?? Text(movie, "shortDescription") ?? "",
                year, Text(movie, "primaryGenreName") ?? "", "Movie", poster,
                Text(movie, "artistName"), null, runtime, released, "Apple iTunes");
        }
        return null;
    }

    public async Task<string?> DownloadPosterAsync(string? posterUrl, string postersRoot, CancellationToken token = default)
    {
        if (!Uri.TryCreate(posterUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode || (response.Content.Headers.ContentLength ?? 0) > 5_242_880)
            return null;
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        var extension = mediaType switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", _ => null };
        if (extension is null) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length == 0 || bytes.Length > 5_242_880) return null;
        var valid = mediaType switch {
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/png" => bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/webp" => bytes.Length >= 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
            _ => false
        };
        if (!valid) return null;
        Directory.CreateDirectory(postersRoot);
        var fileName = "lookup-" + Guid.NewGuid().ToString("N") + extension;
        await File.WriteAllBytesAsync(Path.Combine(postersRoot, fileName), bytes, token);
        return "/posters/" + fileName;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? StripHtml(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value, "<[^>]+>", " ").Replace("&nbsp;", " ").Replace("&amp;", "&").Trim();
}
