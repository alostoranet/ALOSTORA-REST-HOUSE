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

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? StripHtml(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value, "<[^>]+>", " ").Replace("&nbsp;", " ").Replace("&amp;", "&").Trim();
}
