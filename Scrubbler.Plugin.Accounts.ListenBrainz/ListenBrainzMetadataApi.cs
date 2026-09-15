using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MetaBrainz.Common;

namespace Scrubbler.Plugin.Accounts.ListenBrainz;

// Endpoints not yet covered by MetaBrainz.ListenBrainz 6.0.0. Calls are serialized by the plugin.
internal sealed class ListenBrainzMetadataApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Dictionary<string, (DateTimeOffset Expires, JsonElement Value)> _cache = [];
    private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
    private static DateTimeOffset _nextMusicBrainzRequest;

    public RateLimitInfo RateLimitInfo { get; private set; }

    public ListenBrainzMetadataApi(Version version) : this(new HttpClient(), true)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Scrubbler/{version} (+https://github.com/Scrubbler-Dev)");
    }

    internal ListenBrainzMetadataApi(HttpClient http, bool ownsClient = false)
    {
        _http = http;
        _ownsClient = ownsClient;
    }

    public Task<JsonElement> GetListenBrainzAsync(string path, string token, bool cache = true) =>
        GetJsonAsync(new Uri("https://api.listenbrainz.org/1/" + path), token, cache);

    public Task<JsonElement> GetMusicBrainzAsync(string path) =>
        GetJsonAsync(new Uri("https://musicbrainz.org/ws/2/" + path), null, true);

    // 6.0.0 serializes SetNowPlayingAsync with CLR property names instead of the API's wire format.
    public async Task SubmitNowPlayingAsync(string artist, string track, string? album, Version version, string token)
    {
        var metadata = new Dictionary<string, object>
        {
            ["artist_name"] = artist,
            ["track_name"] = track,
            ["additional_info"] = new { submission_client = "Scrubbler", submission_client_version = version.ToString() }
        };
        if (!string.IsNullOrWhiteSpace(album)) metadata["release_name"] = album;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.listenbrainz.org/1/submit-listens")
        {
            Content = JsonContent.Create(new { listen_type = "playing_now", payload = new[] { new { track_metadata = metadata } } })
        };
        request.Headers.Authorization = new("Token", token);
        using var response = await _http.SendAsync(request);
        RateLimitInfo = new RateLimitInfo(response.Headers);
        await response.EnsureSuccessfulAsync();
    }

    private async Task<JsonElement> GetJsonAsync(Uri uri, string? token, bool cache)
    {
        var key = uri.AbsoluteUri;
        if (cache && _cache.TryGetValue(key, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
            return entry.Value;

        var musicBrainz = uri.Host == "musicbrainz.org";
        if (musicBrainz) await MusicBrainzGate.WaitAsync();
        try
        {
            if (musicBrainz)
            {
                var delay = _nextMusicBrainzRequest - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
                _nextMusicBrainzRequest = DateTimeOffset.UtcNow.AddSeconds(1.1);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Never forward the ListenBrainz credential to MusicBrainz.
            if (!musicBrainz && token != null) request.Headers.Authorization = new("Token", token);
            using var response = await _http.SendAsync(request);
            if (!musicBrainz) RateLimitInfo = new RateLimitInfo(response.Headers);
            await response.EnsureSuccessfulAsync();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var value = document.RootElement.Clone();
            if (cache)
            {
                if (_cache.Count >= 256) _cache.Clear();
                _cache[key] = (DateTimeOffset.UtcNow.AddMinutes(10), value);
            }
            return value;
        }
        catch (Exception ex) when (musicBrainz && ex is HttpError or HttpRequestException or OperationCanceledException)
        {
            throw new AccountFunctionException("The MusicBrainz metadata request failed. Please try again later.");
        }
        finally { if (musicBrainz) MusicBrainzGate.Release(); }
    }

    public void ClearCache() => _cache.Clear();

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

internal sealed class AccountFunctionException(string message) : Exception(message);
