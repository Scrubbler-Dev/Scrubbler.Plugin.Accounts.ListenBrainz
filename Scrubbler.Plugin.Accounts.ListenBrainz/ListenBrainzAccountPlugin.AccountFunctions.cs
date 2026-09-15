using System.Text;
using System.Text.Json;
using MetaBrainz.ListenBrainz;
using MetaBrainz.ListenBrainz.Objects;
using Scrubbler.PluginBase.Services;
using ListenBrainzClient = MetaBrainz.ListenBrainz.ListenBrainz;

namespace Scrubbler.Plugin.Accounts.ListenBrainz;

public sealed partial class ListenBrainzAccountPlugin
{
    private readonly ListenBrainzMetadataApi _metadataApi;
    private readonly ILinkOpenerService? _linkOpener;
    private readonly Dictionary<string, StatisticsSnapshot> _statistics = [];

    public async Task<string?> UpdateNowPlaying(string artistName, string trackName, string? albumName)
    {
        var (error, _) = await RunAccountFunctionAsync(async () =>
        {
            RequireNames(artistName, trackName);
            if (!IsScrobblingEnabled) throw new AccountFunctionException("Scrobbling to ListenBrainz is disabled.");
            await WaitForRateLimitAsync();
            await _metadataApi.SubmitNowPlayingAsync(artistName, trackName, albumName, Version, _client.UserToken!);
            return true;
        }, false);
        return error;
    }

    public async Task<string?> SetLoveState(string artistName, string trackName, string? albumName, bool isLoved)
    {
        var (error, _) = await RunAccountFunctionAsync(async () =>
        {
            var recording = await LookupRecordingAsync(artistName, trackName, albumName);
            await WaitForRateLimitAsync();
            await _client.SubmitRecordingFeedbackAsync(new SubmittedRecordingFeedback
            {
                Id = RecordingId(recording),
                Score = isLoved ? 1 : 0
            });
            return true;
        }, false);
        return error;
    }

    public Task<(string? errorMessage, bool isLoved)> GetLoveState(string artistName, string trackName, string? albumName) =>
        RunAccountFunctionAsync(async () =>
        {
            var recording = await LookupRecordingAsync(artistName, trackName, albumName);
            var id = RecordingId(recording);
            await WaitForRateLimitAsync();
            var result = await _metadataApi.GetListenBrainzAsync(
                $"feedback/user/{Uri.EscapeDataString(AccountId!)}/get-feedback-for-recordings?recording_mbids={id:D}",
                _client.UserToken!, cache: false);
            if (!result.TryGetProperty("feedback", out var feedback) || feedback.ValueKind != JsonValueKind.Array)
                throw new AccountFunctionException("ListenBrainz returned an invalid feedback response.");
            foreach (var item in feedback.EnumerateArray())
            {
                if (Guid.TryParse(Text(item, "recording_mbid"), out var feedbackId) && feedbackId == id)
                    return item.GetProperty("score").GetInt32() == 1;
            }
            return false;
        }, false);

    public Task<(string? errorMessage, int playCount)> GetArtistPlayCount(string artistName) =>
        GetPlayCountAsync("artists", artistName, null);

    public Task<(string? errorMessage, int playCount)> GetTrackPlayCount(string artistName, string trackName) =>
        GetPlayCountAsync("recordings", artistName, trackName);

    public Task<(string? errorMessage, int playCount)> GetAlbumPlayCount(string artistName, string albumName) =>
        GetPlayCountAsync("releases", artistName, albumName);

    private Task<(string? errorMessage, int playCount)> GetPlayCountAsync(string kind, string artist, string? name) =>
        RunAccountFunctionAsync(async () =>
        {
            RequireNames(artist);
            if (kind != "artists") RequireNames(name);
            var rows = await GetStatisticsAsync(kind);
            return rows.Where(row => SameName(row.Artist, artist) && (kind == "artists" || SameName(row.Name, name)))
                .Sum(row => row.ListenCount);
        }, 0);

    private async Task<IReadOnlyList<CountRow>> GetStatisticsAsync(string kind)
    {
        if (_statistics.TryGetValue(kind, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Rows;

        var rows = new List<CountRow>();
        var offset = 0;
        DateTimeOffset? lastUpdated = null;
        while (true)
        {
            await WaitForRateLimitAsync();
            var page = await GetStatisticsPageAsync(kind, offset);
            if (page == null) throw new AccountFunctionException("ListenBrainz has not calculated these statistics yet.");
            if (page.Offset is int returnedOffset && returnedOffset != offset)
                throw new AccountFunctionException("ListenBrainz returned an unexpected statistics page. Please try again later.");
            if (lastUpdated is not null && page.LastUpdated != lastUpdated)
                throw new AccountFunctionException("ListenBrainz statistics changed while loading. Please try again.");
            lastUpdated = page.LastUpdated;
            rows.AddRange(page.Rows);
            offset += page.Rows.Count;
            if (page.TotalCount is int total && offset >= total) break;
            if (page.Rows.Count == 0)
            {
                if (page.TotalCount is not null)
                    throw new AccountFunctionException("ListenBrainz returned incomplete statistics. Please try again later.");
                break;
            }
            if (page.TotalCount == null && page.Rows.Count < ListenBrainzClient.MaxItemsPerGet) break;
        }
        _statistics[kind] = new(DateTimeOffset.UtcNow.AddMinutes(10), rows);
        return rows;
    }

    private async Task<StatisticsPage?> GetStatisticsPageAsync(string kind, int offset)
    {
        var user = Uri.EscapeDataString(AccountId!);
        switch (kind)
        {
            case "artists":
                var artists = await _client.GetArtistStatisticsAsync(user, ListenBrainzClient.MaxItemsPerGet, offset, StatisticsRange.AllTime);
                return artists?.Artists == null ? null : new(artists.Artists.Select(a => new CountRow(a.Name, null, a.ListenCount)).ToArray(), artists.TotalCount, artists.Offset, artists.LastUpdated);
            case "recordings":
                var tracks = await _client.GetRecordingStatisticsAsync(user, ListenBrainzClient.MaxItemsPerGet, offset, StatisticsRange.AllTime);
                return tracks?.Recordings == null ? null : new(tracks.Recordings.Select(t => new CountRow(t.ArtistName, t.Name, t.ListenCount)).ToArray(), tracks.TotalCount, tracks.Offset, tracks.LastUpdated);
            default:
                var albums = await _client.GetReleaseStatisticsAsync(user, ListenBrainzClient.MaxItemsPerGet, offset, StatisticsRange.AllTime);
                return albums?.Releases == null ? null : new(albums.Releases.Select(a => new CountRow(a.ArtistName, a.Name, a.ListenCount)).ToArray(), albums.TotalCount, albums.Offset, albums.LastUpdated);
        }
    }

    public Task<(string? errorMessage, IEnumerable<string> tags)> GetTrackTags(string artistName, string trackName) =>
        RunAccountFunctionAsync<IEnumerable<string>>(async () =>
        {
            var recording = await LookupRecordingAsync(artistName, trackName, null);
            if (recording.TryGetProperty("metadata", out var metadata) &&
                metadata.TryGetProperty("tag", out var tag) && tag.TryGetProperty("recording", out var tags))
                return ReadTags(tags, "tag");
            return Array.Empty<string>();
        }, Array.Empty<string>());

    public Task<(string? errorMessage, IEnumerable<string> tags)> GetArtistTags(string artistName) =>
        GetMusicBrainzTagsAsync("artist", artistName, null);

    public Task<(string? errorMessage, IEnumerable<string> tags)> GetAlbumTags(string artistName, string albumName) =>
        GetMusicBrainzTagsAsync("release-group", artistName, albumName);

    private Task<(string? errorMessage, IEnumerable<string> tags)> GetMusicBrainzTagsAsync(string entity, string artist, string? album) =>
        RunAccountFunctionAsync<IEnumerable<string>>(async () =>
        {
            RequireNames(artist);
            if (entity == "release-group") RequireNames(album);
            var query = entity == "artist" ? $"artist:{QuoteSearch(artist)}" :
                $"releasegroup:{QuoteSearch(album!)} AND artist:{QuoteSearch(artist)}";
            var search = await _metadataApi.GetMusicBrainzAsync($"{entity}/?query={Uri.EscapeDataString(query)}&fmt=json&limit=100");
            var property = entity == "artist" ? "artists" : "release-groups";
            if (!search.TryGetProperty(property, out var results) || results.ValueKind != JsonValueKind.Array)
                throw new AccountFunctionException("MusicBrainz returned an invalid search response.");
            var matches = results.EnumerateArray().Where(item => entity == "artist"
                    ? SameName(Text(item, "name"), artist)
                    : SameName(Text(item, "title"), album) && SameName(ArtistCredit(item), artist))
                .Select(item => Text(item, "id")).Where(id => Guid.TryParse(id, out _)).Distinct().ToArray();
            if (matches.Length != 1)
                throw new AccountFunctionException(matches.Length == 0
                    ? "No matching artist or album was found in MusicBrainz."
                    : "The artist or album name is ambiguous in MusicBrainz; tags cannot be selected reliably.");
            var data = await _metadataApi.GetMusicBrainzAsync($"{entity}/{matches[0]}?inc=tags&fmt=json");
            return data.TryGetProperty("tags", out var tags) ? ReadTags(tags, "name") : Array.Empty<string>();
        }, Array.Empty<string>());

    private async Task<JsonElement> LookupRecordingAsync(string artist, string track, string? album)
    {
        RequireNames(artist, track);
        var path = $"metadata/lookup/?artist_name={Uri.EscapeDataString(artist.Trim())}&recording_name={Uri.EscapeDataString(track.Trim())}";
        if (!string.IsNullOrWhiteSpace(album)) path += "&release_name=" + Uri.EscapeDataString(album.Trim());
        await WaitForRateLimitAsync();
        var result = await _metadataApi.GetListenBrainzAsync(path + "&metadata=true&inc=tag", _client.UserToken!);
        _ = RecordingId(result);
        // The mapper can return fuzzy matches. Do not love a different recording silently.
        if (!SameName(Text(result, "artist_credit_name"), artist) || !SameName(Text(result, "recording_name"), track))
            throw new AccountFunctionException("ListenBrainz could not identify this artist and track reliably.");
        return result;
    }

    private static Guid RecordingId(JsonElement recording)
    {
        if (Guid.TryParse(Text(recording, "recording_mbid"), out var id) && id != Guid.Empty) return id;
        throw new AccountFunctionException("No matching recording was found in ListenBrainz.");
    }

    public Task OpenArtistLink(string artistName) => OpenSearchLinkAsync("artist", artistName);

    public Task OpenAlbumLink(string albumName, string artistName) =>
        OpenSearchLinkAsync("album", albumName, artistName);

    public Task OpenTrackLink(string trackName, string artistName, string? albumName) =>
        OpenSearchLinkAsync("track", trackName, artistName);

    public Task OpenTagLink(string tagName) => string.IsNullOrWhiteSpace(tagName) ? Task.CompletedTask :
        OpenLinkAsync("https://musicbrainz.org/tag/" + Uri.EscapeDataString(tagName.Trim()));

    private Task OpenSearchLinkAsync(string type, string name, string? artist = null)
    {
        if (string.IsNullOrWhiteSpace(name) || (type != "artist" && string.IsNullOrWhiteSpace(artist))) return Task.CompletedTask;
        var term = artist == null ? name.Trim() : $"{artist.Trim()} {name.Trim()}";
        return OpenLinkAsync($"https://listenbrainz.org/search/?search_type={type}&search_term={Uri.EscapeDataString(term)}");
    }

    private async Task OpenLinkAsync(string url)
    {
        try
        {
            if (_linkOpener != null) await _linkOpener.OpenLink(url);
        }
        catch (Exception ex) { _logService.Warn($"Could not open ListenBrainz account link ({ex.GetType().Name})."); }
    }

    private async Task<(string? errorMessage, T value)> RunAccountFunctionAsync<T>(Func<Task<T>> action, T failureValue)
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsAuthenticated) return ("Not connected to ListenBrainz. Connect your account first.", failureValue);
            return (null, await action());
        }
        catch (Exception ex)
        {
            _logService.Warn($"ListenBrainz account function failed ({ex.GetType().Name}).");
            return (DescribeError(ex), failureValue);
        }
        finally { _gate.Release(); }
    }

    private void ClearAccountFunctionCaches()
    {
        _statistics.Clear();
        _metadataApi.ClearCache();
    }

    private static void RequireNames(params string?[] names)
    {
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new AccountFunctionException("Artist, track, or album name is missing.");
    }

    private static bool SameName(string? first, string? second) => first != null && second != null &&
        string.Equals(first.Trim().Normalize(NormalizationForm.FormC), second.Trim().Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement item, string property) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ArtistCredit(JsonElement item) => item.TryGetProperty("artist-credit", out var credit) && credit.ValueKind == JsonValueKind.Array
        ? string.Concat(credit.EnumerateArray().Select(c => (Text(c, "name") ?? (c.TryGetProperty("artist", out var a) ? Text(a, "name") : null)) + Text(c, "joinphrase")))
        : "";

    private static string QuoteSearch(string value) => "\"" + value.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string[] ReadTags(JsonElement tags, string nameProperty) => tags.ValueKind != JsonValueKind.Array ? [] :
        tags.EnumerateArray().Select(t => (Name: Text(t, nameProperty), Count: t.TryGetProperty("count", out var c) && c.TryGetInt32(out var count) ? count : 0))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name) && t.Count > 0).OrderByDescending(t => t.Count)
            .Select(t => t.Name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed record CountRow(string? Artist, string? Name, int ListenCount);
    private sealed record StatisticsPage(IReadOnlyList<CountRow> Rows, int? TotalCount, int? Offset, DateTimeOffset LastUpdated);
    private sealed record StatisticsSnapshot(DateTimeOffset Expires, IReadOnlyList<CountRow> Rows);
}
