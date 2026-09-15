using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Scrubbler.PluginBase.Plugin.Account;
using Scrubbler.PluginBase.Services;

namespace Scrubbler.Test.ListenBrainzTest;

public sealed partial class ListenBrainzAccountPluginTests
{
    private const string RecordingMbid = "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae";
    private const string EntityMbid = "db92a151-1ac2-438b-bc43-b82e149ddd50";
    private const string Mapping = """
        {"artist_credit_name":"Artist","recording_name":"Track","recording_mbid":"8f3471b5-7e6a-48da-86a9-c1c07a0f47ae",
         "metadata":{"tag":{"recording":[{"tag":"rock","count":2},{"tag":"jazz","count":5},{"tag":"Rock","count":1},{"tag":"downvoted","count":-1}]}}}
        """;

    [Test]
    public void ContainerExposesAllFiveCapabilities()
    {
        var plugin = Create(new RecordingHandler());
        var container = new AccountFunctionContainer(plugin);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(container.UpdateNowPlayingObject, Is.SameAs(plugin));
            Assert.That(container.LoveTrackObject, Is.SameAs(plugin));
            Assert.That(container.FetchPlayCountsObject, Is.SameAs(plugin));
            Assert.That(container.FetchTagsObject, Is.SameAs(plugin));
            Assert.That(container.OpenLinksObject, Is.SameAs(plugin));
            Assert.That(plugin, Is.Not.InstanceOf<IHaveScrobbleLimit>());
        }
    }

    [Test]
    public async Task NowPlayingHasNoTimestampAndRequiresScrobblingEnabled()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        Assert.That(await plugin.UpdateNowPlaying("Artist", "Track", ""), Does.Contain("disabled"));
        plugin.IsScrobblingEnabled = true;
        Assert.That(await plugin.UpdateNowPlaying("Artist", "Track", ""), Is.Null);
        using var payload = JsonDocument.Parse(handler.Requests.Last().Body!);
        var listen = payload.RootElement.GetProperty("payload")[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.RootElement.GetProperty("listen_type").GetString(), Is.EqualTo("playing_now"));
            Assert.That(listen.TryGetProperty("listened_at", out _), Is.False);
            Assert.That(listen.GetProperty("track_metadata").GetProperty("track_name").GetString(), Is.EqualTo("Track"));
            Assert.That(listen.GetProperty("track_metadata").TryGetProperty("release_name", out _), Is.False);
            Assert.That(handler.Requests.Last().Authorization, Is.EqualTo("Token " + Token));
            Assert.That(handler.Requests, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public async Task DisconnectedFunctionsReturnErrorsWithoutRequests()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await plugin.UpdateNowPlaying("Artist", "Track", null), Is.Not.Null);
            Assert.That(await plugin.SetLoveState("Artist", "Track", null, true), Is.Not.Null);
            Assert.That((await plugin.GetLoveState("Artist", "Track", null)).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetArtistPlayCount("Artist")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetTrackPlayCount("Artist", "Track")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetAlbumPlayCount("Artist", "Album")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetArtistTags("Artist")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetTrackTags("Artist", "Track")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetAlbumTags("Artist", "Album")).errorMessage, Is.Not.Null);
            Assert.That(handler.Requests, Is.Empty);
        }
    }

    [Test]
    public async Task BlankFunctionInputsDoNotSendRequests()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        plugin.IsScrobblingEnabled = true;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await plugin.UpdateNowPlaying(" ", "Track", null), Is.Not.Null);
            Assert.That(await plugin.SetLoveState("Artist", " ", null, true), Is.Not.Null);
            Assert.That((await plugin.GetLoveState("Artist", "", null)).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetArtistPlayCount(" ")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetTrackPlayCount("Artist", " ")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetAlbumPlayCount("Artist", " ")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetArtistTags(" ")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetTrackTags("Artist", " ")).errorMessage, Is.Not.Null);
            Assert.That((await plugin.GetAlbumTags("Artist", " ")).errorMessage, Is.Not.Null);
            Assert.That(handler.Requests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task LoveAndUnloveResolveRecordingAndSendOneAndZeroScores()
    {
        var handler = new RecordingHandler { Respond = r => JsonResponse(r.Uri.AbsolutePath.Contains("lookup") ? Mapping : "{\"status\":\"ok\"}") };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        Assert.That(await plugin.SetLoveState("Artist", "Track", "Album & live", true), Is.Null);
        Assert.That(await plugin.SetLoveState("Artist", "Track", "Album & live", false), Is.Null);
        var writes = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(writes, Has.Length.EqualTo(2));
            Assert.That(handler.Requests.Count(r => r.Uri.AbsolutePath.Contains("lookup")), Is.EqualTo(1));
            Assert.That(Uri.UnescapeDataString(handler.Requests[1].Uri.Query), Does.Contain("release_name=Album & live"));
            Assert.That(handler.Requests[1].Authorization, Is.EqualTo("Token " + Token));
            Assert.That(handler.Requests[1].UserAgent, Does.Contain("Scrubbler"));
        }
        for (var i = 0; i < writes.Length; i++)
        {
            using var json = JsonDocument.Parse(writes[i].Body!);
            Assert.That(writes[i].Uri.AbsolutePath, Is.EqualTo("/1/feedback/recording-feedback"));
            Assert.That(json.RootElement.GetProperty("recording_mbid").GetString(), Is.EqualTo(RecordingMbid));
            Assert.That(json.RootElement.GetProperty("score").GetInt32(), Is.EqualTo(i == 0 ? 1 : 0));
        }
    }

    [TestCase(1, true)]
    [TestCase(0, false)]
    [TestCase(-1, false)]
    public async Task LovedStateUsesTheCurrentUsersRecordingFeedback(int score, bool expected)
    {
        var handler = new RecordingHandler { Respond = r => JsonResponse(r.Uri.AbsolutePath.Contains("lookup") ? Mapping :
            JsonSerializer.Serialize(new { feedback = new[] { new { recording_mbid = RecordingMbid, score } } })) };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        var result = await plugin.GetLoveState("Artist", "Track", null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.errorMessage, Is.Null);
            Assert.That(result.isLoved, Is.EqualTo(expected));
            Assert.That(handler.Requests.Last().Uri.AbsolutePath, Is.EqualTo("/1/feedback/user/listener/get-feedback-for-recordings"));
            Assert.That(handler.Requests.Last().Uri.Query, Does.Contain("recording_mbids=" + RecordingMbid));
        }
    }

    [Test]
    public async Task FeedbackIsRefetchedAfterLoveChanges()
    {
        var score = 0;
        var handler = new RecordingHandler { Respond = r =>
        {
            if (r.Uri.AbsolutePath.Contains("lookup")) return JsonResponse(Mapping);
            if (r.Method == HttpMethod.Post) { score = 1; return JsonResponse("{\"status\":\"ok\"}"); }
            return JsonResponse(JsonSerializer.Serialize(new { feedback = new[] { new { recording_mbid = RecordingMbid, score } } }));
        } };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        Assert.That((await plugin.GetLoveState("Artist", "Track", null)).isLoved, Is.False);
        await plugin.SetLoveState("Artist", "Track", null, true);
        Assert.That((await plugin.GetLoveState("Artist", "Track", null)).isLoved, Is.True);
    }

    [TestCase("{}")]
    [TestCase("{\"recording_mbid\":\"not-a-guid\"}")]
    [TestCase("{\"recording_mbid\":\"8f3471b5-7e6a-48da-86a9-c1c07a0f47ae\",\"recording_name\":\"Different song\",\"artist_credit_name\":\"Artist\"}")]
    public async Task UnknownOrDifferentRecordingDoesNotSubmitFeedback(string mapping)
    {
        var handler = new RecordingHandler { Respond = _ => JsonResponse(mapping) };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        Assert.That(await plugin.SetLoveState("Artist", "Track", null, true), Is.Not.Null);
        Assert.That(handler.Requests.Any(r => r.Method == HttpMethod.Post), Is.False);
    }

    [Test]
    public async Task TrackTagsAreRankedDeduplicatedAndCached()
    {
        var handler = new RecordingHandler { Respond = _ => JsonResponse(Mapping) };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        var result = await plugin.GetTrackTags("Artist", "Track");
        Assert.That(result.errorMessage, Is.Null);
        Assert.That(result.tags, Is.EqualTo(new[] { "jazz", "rock" }));
        await plugin.GetTrackTags("Artist", "Track");
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
    }

    [TestCase("artist")]
    [TestCase("album")]
    public async Task ArtistAndAlbumTagsUseMusicBrainzWithoutSendingToken(string kind)
    {
        var handler = new RecordingHandler { Respond = r =>
        {
            if (r.Uri.Query.Contains("query=")) return JsonResponse(kind == "artist"
                ? "{\"artists\":[{\"id\":\"" + EntityMbid + "\",\"name\":\"Artist\"}]}"
                : "{\"release-groups\":[{\"id\":\"" + EntityMbid + "\",\"title\":\"Album\",\"artist-credit\":[{\"name\":\"Artist\"}]}]}");
            return JsonResponse("{\"tags\":[{\"name\":\"rock\",\"count\":3}]}");
        } };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        var result = kind == "artist" ? await plugin.GetArtistTags("Artist") : await plugin.GetAlbumTags("Artist", "Album");
        Assert.That(result.errorMessage, Is.Null);
        Assert.That(result.tags, Is.EqualTo(new[] { "rock" }));
        var requests = handler.Requests.Skip(1).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Has.Length.EqualTo(2));
            Assert.That(requests.All(r => r.Uri.Host == "musicbrainz.org"), Is.True);
            Assert.That(requests.All(r => r.Authorization == null), Is.True);
            Assert.That(requests.All(r => r.UserAgent.Contains("Scrubbler")), Is.True);
            Assert.That(requests.Last().Uri.Query, Does.Contain("inc=tags"));
        }
    }

    [Test]
    public async Task AmbiguousArtistDoesNotFetchArbitraryTags()
    {
        var handler = new RecordingHandler { Respond = _ => JsonResponse(
            "{\"artists\":[{\"id\":\"" + EntityMbid + "\",\"name\":\"Artist\"},{\"id\":\"" + RecordingMbid + "\",\"name\":\"Artist\"}]}") };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        Assert.That((await plugin.GetArtistTags("Artist")).errorMessage, Does.Contain("ambiguous"));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
    }

    [TestCase("artists")]
    [TestCase("recordings")]
    [TestCase("releases")]
    public async Task PersonalCountsPaginateSumMatchingRowsAndCache(string kind)
    {
        var handler = new RecordingHandler { Respond = r => StatisticsResponse(kind, r.Uri.Query.Contains("offset=0") ? 0 : 2) };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        async Task<(string? errorMessage, int playCount)> GetCount() => kind switch
        {
            "artists" => await plugin.GetArtistPlayCount(" artist "),
            "recordings" => await plugin.GetTrackPlayCount("Artist", "track"),
            _ => await plugin.GetAlbumPlayCount("Artist", "album")
        };
        var result = await GetCount();
        Assert.That(result.errorMessage, Is.Null);
        Assert.That(result.playCount, Is.EqualTo(7));
        await GetCount();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Requests, Has.Count.EqualTo(3));
            Assert.That(handler.Requests.Last().Uri.Query, Does.Contain("offset=2"));
            Assert.That(handler.Requests.Last().Uri.Query, Does.Contain("range=all_time"));
            Assert.That(handler.Requests.Last().Uri.AbsolutePath, Is.EqualTo("/1/stats/user/listener/" + kind));
        }
    }

    [Test]
    public async Task StatisticsCacheIsClearedWhenChangingAccount()
    {
        var count = 2;
        var handler = new RecordingHandler { Respond = _ => JsonResponse(JsonSerializer.Serialize(new
        {
            payload = new { artists = new[] { new { artist_name = "Artist", listen_count = count } }, count = 1,
                offset = 0, total_artist_count = 1, range = "all_time", last_updated = 1600000000, user_id = "listener" }
        })) };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        Assert.That((await plugin.GetArtistPlayCount("Artist")).playCount, Is.EqualTo(2));
        await plugin.LogoutAsync();
        Assert.That((await plugin.GetArtistPlayCount("Artist")).errorMessage, Is.Not.Null);
        count = 30;
        handler.ValidationJson = "{\"code\":200,\"message\":\"Token valid.\",\"valid\":true,\"user_name\":\"other-user\"}";
        await plugin.LoginWithTokenAsync("another-token");
        Assert.That((await plugin.GetArtistPlayCount("Artist")).playCount, Is.EqualTo(30));
        Assert.That(handler.Requests.Last().Uri.AbsolutePath, Does.Contain("/other-user/"));
    }

    [Test]
    public async Task MissingStatisticsReturnsUnavailableInsteadOfZero()
    {
        var plugin = Create(new RecordingHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent) });
        await plugin.LoginWithTokenAsync(Token);
        Assert.That((await plugin.GetArtistPlayCount("Artist")).errorMessage, Does.Contain("not calculated"));
    }

    [Test]
    public async Task EmptyStatisticsReturnsZeroAndIncompleteStatisticsReturnsError()
    {
        var total = 0;
        var plugin = Create(new RecordingHandler { Respond = _ => JsonResponse(
            "{\"payload\":{\"artists\":[],\"count\":0,\"offset\":0,\"total_artist_count\":" + total + ",\"range\":\"all_time\",\"last_updated\":1600000000,\"user_id\":\"listener\"}}") });
        await plugin.LoginWithTokenAsync(Token);
        Assert.That(await plugin.GetArtistPlayCount("Artist"), Is.EqualTo(((string?)null, 0)));
        await plugin.LogoutAsync();
        await plugin.LoginWithTokenAsync(Token);
        total = 10;
        Assert.That((await plugin.GetArtistPlayCount("Artist")).errorMessage, Does.Contain("incomplete"));
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task FunctionFailuresDoNotThrowOrLeakTokens(HttpStatusCode status)
    {
        var handler = new RecordingHandler { Respond = _ => JsonResponse("{\"error\":\"" + Token + "\"}", status) };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        var result = await plugin.GetLoveState("Artist", "Track", null);
        Assert.That(result.errorMessage, Is.Not.Null.And.Not.Contains(Token));
        AssertLogsDoNotContainToken();
    }

    [Test]
    public async Task RepeatedStatisticsPageDoesNotDoubleCount()
    {
        var plugin = Create(new RecordingHandler { Respond = _ => StatisticsResponse("artists", 0) });
        await plugin.LoginWithTokenAsync(Token);
        var result = await plugin.GetArtistPlayCount("Artist");
        Assert.That(result.errorMessage, Does.Contain("unexpected statistics page"));
        Assert.That(result.playCount, Is.Zero);
    }

    [Test]
    public async Task MetadataRateLimitIsRespectedBeforeFeedbackSubmission()
    {
        DateTimeOffset lookedUp = default;
        DateTimeOffset submitted = default;
        var handler = new RecordingHandler { Respond = r =>
        {
            if (r.Uri.AbsolutePath.Contains("lookup"))
            {
                lookedUp = DateTimeOffset.UtcNow;
                var response = JsonResponse(Mapping);
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset-In", "1");
                return response;
            }
            submitted = DateTimeOffset.UtcNow;
            return JsonResponse("{\"status\":\"ok\"}");
        } };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        Assert.That(await plugin.SetLoveState("Artist", "Track", null, true), Is.Null);
        Assert.That(submitted - lookedUp, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task LinksUseEscapedServiceSearchesAndTagPages()
    {
        var opener = new Mock<ILinkOpenerService>();
        var links = new List<string>();
        opener.Setup(o => o.OpenLink(It.IsAny<string>())).Callback<string>(links.Add).Returns(Task.CompletedTask);
        var plugin = Create(new RecordingHandler(), linkOpener: opener.Object);
        await plugin.OpenArtistLink("AC/DC & Friends");
        await plugin.OpenAlbumLink("Album #1", "Artist");
        await plugin.OpenTrackLink("Track?", "Artist", null);
        await plugin.OpenTagLink("rock/pop");
        await plugin.OpenArtistLink(" ");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(links, Has.Count.EqualTo(4));
            Assert.That(links[0], Is.EqualTo("https://listenbrainz.org/search/?search_type=artist&search_term=AC%2FDC%20%26%20Friends"));
            Assert.That(links[1], Does.Contain("search_type=album").And.Contains("Artist%20Album%20%231"));
            Assert.That(links[2], Does.Contain("search_type=track").And.Contains("Artist%20Track%3F"));
            Assert.That(links[3], Is.EqualTo("https://musicbrainz.org/tag/rock%2Fpop"));
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage StatisticsResponse(string kind, int offset)
    {
        var rows = offset == 0 ? new[] { StatRow(kind, "Artist", 2), StatRow(kind, "Other artist", 100) }
            : new[] { StatRow(kind, "Artist", 5) };
        var totalName = kind switch { "artists" => "total_artist_count", "recordings" => "total_recording_count", _ => "total_release_count" };
        return JsonResponse(JsonSerializer.Serialize(new
        {
            payload = new Dictionary<string, object>
            {
                [kind] = rows, ["count"] = rows.Length, ["offset"] = offset, [totalName] = 3,
                ["range"] = "all_time", ["last_updated"] = 1600000000, ["user_id"] = "listener"
            }
        }));
    }

    private static Dictionary<string, object> StatRow(string kind, string artist, int count)
    {
        var row = new Dictionary<string, object> { ["artist_name"] = artist, ["listen_count"] = count };
        if (kind == "recordings") row["track_name"] = "Track";
        if (kind == "releases") row["release_name"] = "Album";
        return row;
    }
}
