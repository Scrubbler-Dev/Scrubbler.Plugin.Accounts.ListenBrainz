using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Scrubbler.Plugin.Accounts.ListenBrainz;
using Scrubbler.PluginBase;
using Scrubbler.PluginBase.Services;
using Scrubbler.PluginBase.Settings;
using ListenBrainzClient = MetaBrainz.ListenBrainz.ListenBrainz;

namespace Scrubbler.Test.ListenBrainzTest;

public sealed partial class ListenBrainzAccountPluginTests
{
    private const string Token = "11111111-2222-3333-4444-555555555555";
    private string _directory = null!;
    private readonly List<ListenBrainzAccountPlugin> _plugins = [];
    private Mock<ILogService> _log = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Scrubbler.ListenBrainz.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _log = new Mock<ILogService>();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var plugin in _plugins) plugin.Dispose();
        _plugins.Clear();
        Directory.Delete(_directory, true);
    }

    private ListenBrainzAccountPlugin Create(RecordingHandler handler,
        Func<Func<string, Task<string?>>, Task>? showDialog = null, ISecureStore? secureStore = null,
        ILinkOpenerService? linkOpener = null)
    {
        var factory = new Mock<IModuleLogServiceFactory>();
        factory.Setup(f => f.Create(It.IsAny<string>())).Returns(_log.Object);
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Scrubbler/1.0 (+https://github.com/Scrubbler-Dev)");
        var plugin = new ListenBrainzAccountPlugin(factory.Object, new ListenBrainzClient(http, true),
            secureStore ?? new FileSecureStore(Path.Combine(_directory, "settings.dat"), "ListenBrainz"),
            new JsonSettingsStore(Path.Combine(_directory, "settings.json")), showDialog ?? (_ => Task.CompletedTask),
            new ListenBrainzMetadataApi(http), linkOpener);
        _plugins.Add(plugin);
        return plugin;
    }

    [Test]
    public async Task LoginPersistsTokenAndIdentityAndRestoresWithoutNetwork()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.LoadAsync();
        Assert.That(await plugin.LoginWithTokenAsync("  " + Token + "  "), Is.Null);
        plugin.IsScrobblingEnabled = true;
        await plugin.SaveAsync();
        var restoredHandler = new RecordingHandler();
        var restored = Create(restoredHandler);
        await restored.LoadAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restored.IsAuthenticated, Is.True);
            Assert.That(restored.AccountId, Is.EqualTo("listener"));
            Assert.That(restored.IsScrobblingEnabled, Is.True);
            Assert.That(restoredHandler.Requests, Is.Empty);
            Assert.That(handler.Requests.Single().Authorization, Is.EqualTo("Token " + Token));
            Assert.That(File.ReadAllText(Path.Combine(_directory, "settings.json")), Does.Not.Contain(Token));
            Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_directory, "settings.dat"))), Does.Not.Contain(Token));
        }
        Assert.That((await restored.ScrobbleAsync([Track()])).Success, Is.True);
        Assert.That(restoredHandler.Requests.Single().Authorization, Is.EqualTo("Token " + Token));
    }

    [TestCase("{\"code\":200,\"message\":\"Token invalid.\",\"valid\":false}")]
    [TestCase("{\"code\":200,\"message\":\"Token valid.\",\"valid\":true}")]
    public async Task InvalidOrIncompleteValidationDoesNotAuthenticate(string response)
    {
        var plugin = Create(new RecordingHandler { ValidationJson = response });
        Assert.That(await plugin.LoginWithTokenAsync(Token), Is.Not.Null);
        Assert.That(plugin.IsAuthenticated, Is.False);
        Assert.That(File.Exists(Path.Combine(_directory, "settings.dat")), Is.False);
    }

    [Test]
    public async Task EmptyTokenAndCancelledDialogDoNotSendRequests()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.AuthenticateAsync();
        Assert.That(await plugin.LoginWithTokenAsync("  "), Is.Not.Null);
        Assert.That(plugin.IsAuthenticated, Is.False);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task AuthenticationEntryPointUsesTheDialogCallback()
    {
        var plugin = Create(new RecordingHandler(), async login => Assert.That(await login(Token), Is.Null));
        await plugin.AuthenticateAsync();
        Assert.That(plugin.AccountId, Is.EqualTo("listener"));
    }

    [Test]
    public async Task FailedReplacementKeepsPreviousCredentialsAndUsesNewTokenForValidation()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        handler.ValidationJson = "{\"code\":200,\"message\":\"Invalid\",\"valid\":false}";
        Assert.That(await plugin.LoginWithTokenAsync("replacement-token"), Is.Not.Null);
        plugin.IsScrobblingEnabled = true;
        await plugin.ScrobbleAsync([Track()]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plugin.IsAuthenticated, Is.True);
            Assert.That(handler.Requests[1].Authorization, Is.EqualTo("Token replacement-token"));
            Assert.That(handler.Requests[2].Authorization, Is.EqualTo("Token " + Token));
        }
    }

    [Test]
    public async Task StorageFailureDoesNotAuthenticateOrLeakToken()
    {
        var store = new Mock<ISecureStore>();
        store.Setup(s => s.SaveAsync(It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(new IOException(Token));
        var plugin = Create(new RecordingHandler(), secureStore: store.Object);
        var error = await plugin.LoginWithTokenAsync(Token);
        Assert.That(error, Does.Not.Contain(Token));
        Assert.That(plugin.IsAuthenticated, Is.False);
        AssertLogsDoNotContainToken();
    }

    [Test]
    public async Task LogoutClearsMemoryAndDiskImmediately()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        plugin.IsScrobblingEnabled = true;
        await plugin.LogoutAsync();
        var restored = Create(new RecordingHandler());
        await restored.LoadAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plugin.IsAuthenticated, Is.False);
            Assert.That(plugin.AccountId, Is.Null);
            Assert.That(restored.IsAuthenticated, Is.False);
            Assert.That((await plugin.ScrobbleAsync([Track()])).Success, Is.False);
            Assert.That(handler.Requests, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public async Task ScrobblingRequiresAuthenticationAndEnablement()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        plugin.IsScrobblingEnabled = true;
        Assert.That((await plugin.ScrobbleAsync([Track()])).Success, Is.False);
        await plugin.LoginWithTokenAsync(Token);
        plugin.IsScrobblingEnabled = false;
        Assert.That((await plugin.ScrobbleAsync([Track()])).Success, Is.False);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task MapsHistoricalListenAndSubmitsWithAuthenticatedImport()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        plugin.IsScrobblingEnabled = true;
        var track = Track();
        Assert.That((await plugin.ScrobbleAsync([track])).Success, Is.True);
        var request = handler.Requests.Last();
        using var json = JsonDocument.Parse(request.Body!);
        var listen = json.RootElement.GetProperty("payload")[0];
        var metadata = listen.GetProperty("track_metadata");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(request.Uri.AbsolutePath, Is.EqualTo("/1/submit-listens"));
            Assert.That(request.Authorization, Is.EqualTo("Token " + Token));
            Assert.That(request.UserAgent, Does.Contain("Scrubbler/"));
            Assert.That(json.RootElement.GetProperty("listen_type").GetString(), Is.EqualTo("import"));
            Assert.That(listen.GetProperty("listened_at").GetInt64(), Is.EqualTo(track.Timestamp.ToUnixTimeSeconds()));
            Assert.That(metadata.GetProperty("artist_name").GetString(), Is.EqualTo(track.Artist));
            Assert.That(metadata.GetProperty("track_name").GetString(), Is.EqualTo(track.Track));
            Assert.That(metadata.GetProperty("release_name").GetString(), Is.EqualTo(track.Album));
            Assert.That(metadata.GetProperty("additional_info").GetProperty("albumartist").GetString(), Is.EqualTo(track.AlbumArtist));
        }
    }

    [Test]
    public async Task BatchesLargeImportsAndAcceptsMissingAlbum()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        plugin.IsScrobblingEnabled = true;
        var track = Track();
        track.Album = null;
        track.AlbumArtist = null;
        Assert.That((await plugin.ScrobbleAsync(Enumerable.Repeat(track, 1001))).Success, Is.True);
        var counts = handler.Requests.Where(r => r.Body != null).Select(r =>
        {
            using var json = JsonDocument.Parse(r.Body!);
            return json.RootElement.GetProperty("payload").GetArrayLength();
        });
        Assert.That(counts, Is.EqualTo(new[] { 1000, 1 }));
    }

    [Test]
    public async Task EmptyOrInvalidInputDoesNotSendAnyListens()
    {
        var handler = new RecordingHandler();
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        plugin.IsScrobblingEnabled = true;
        Assert.That((await plugin.ScrobbleAsync([])).Success, Is.True);
        var invalid = Track();
        invalid.Artist = " ";
        Assert.That((await plugin.ScrobbleAsync(Enumerable.Repeat(Track(), 1000).Append(invalid))).Success, Is.False);
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.TooManyRequests)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task ReportsPartialFailureWithoutRetrying(HttpStatusCode status)
    {
        var handler = new RecordingHandler { FailSubmission = 2, FailureStatus = status };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        plugin.IsScrobblingEnabled = true;
        var response = await plugin.ScrobbleAsync(Enumerable.Repeat(Track(), 2001));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Success, Is.False);
            Assert.That(response.ErrorMessage, Does.Contain("1000 listens confirmed"));
            Assert.That(response.ErrorMessage, Does.Not.Contain(Token));
            Assert.That(handler.Requests, Has.Count.EqualTo(3));
        }
        AssertLogsDoNotContainToken();
    }

    [Test]
    public async Task NetworkFailureIsReportedWithoutLeakingRequestDetails()
    {
        var handler = new RecordingHandler { NetworkFailure = true };
        var plugin = Create(handler);
        Assert.That(await plugin.LoginWithTokenAsync(Token), Does.Contain("connection"));
        AssertLogsDoNotContainToken();
    }

    [Test]
    public async Task CorruptCredentialsAllowThePluginToLoadDisconnected()
    {
        var store = new Mock<ISecureStore>();
        store.Setup(s => s.GetAsync(It.IsAny<string>())).ReturnsAsync("not-json");
        var handler = new RecordingHandler();
        var plugin = Create(handler, secureStore: store.Object);
        await plugin.LoadAsync();
        Assert.That(plugin.IsAuthenticated, Is.False);
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task WaitsForRateLimitResetBeforeTheNextBatch()
    {
        var handler = new RecordingHandler { RateLimitFirstSubmission = true };
        var plugin = Create(handler);
        await plugin.LoginWithTokenAsync(Token);
        plugin.IsScrobblingEnabled = true;
        Assert.That((await plugin.ScrobbleAsync(Enumerable.Repeat(Track(), 1001))).Success, Is.True);
        Assert.That(handler.SubmissionTimes[1] - handler.SubmissionTimes[0], Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task EnabledEventOnlyFiresOnChangeAndSettingIsPersisted()
    {
        var plugin = Create(new RecordingHandler());
        var changes = 0;
        plugin.IsScrobblingEnabledChanged += (_, _) => changes++;
        plugin.IsScrobblingEnabled = true;
        plugin.IsScrobblingEnabled = true;
        await plugin.SaveAsync();
        var restored = Create(new RecordingHandler());
        await restored.LoadAsync();
        Assert.That(changes, Is.EqualTo(1));
        Assert.That(restored.IsScrobblingEnabled, Is.True);
    }

    private void AssertLogsDoNotContainToken()
    {
        foreach (var invocation in _log.Invocations)
            foreach (var argument in invocation.Arguments)
                Assert.That(argument?.ToString(), Does.Not.Contain(Token));
    }

    private static ScrobbleData Track() => new("Track – 曲", "Artist", new DateTimeOffset(2020, 1, 2, 12, 0, 0, TimeSpan.FromHours(2)))
    { Album = "Album", AlbumArtist = "Album artist" };

    private sealed record Request(HttpMethod Method, Uri Uri, string? Authorization, string UserAgent, string? Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public Func<Request, HttpResponseMessage>? Respond { get; init; }
        public string ValidationJson { get; set; } = "{\"code\":200,\"message\":\"Token valid.\",\"valid\":true,\"user_name\":\"listener\"}";
        public int FailSubmission { get; init; }
        public HttpStatusCode FailureStatus { get; init; }
        public bool NetworkFailure { get; init; }
        public bool RateLimitFirstSubmission { get; init; }
        public List<DateTimeOffset> SubmissionTimes { get; } = [];
        private int _submissions;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Headers.UserAgent.ToString(), request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (NetworkFailure) throw new HttpRequestException("Sensitive request: " + Token);
            var validation = request.RequestUri!.AbsolutePath.EndsWith("validate-token");
            if (!validation && Respond != null)
            {
                var customResponse = Respond(Requests.Last());
                customResponse.RequestMessage = request;
                return customResponse;
            }
            if (!validation) SubmissionTimes.Add(DateTimeOffset.UtcNow);
            var failed = !validation && ++_submissions == FailSubmission;
            var response = new HttpResponseMessage(failed ? FailureStatus : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(validation ? ValidationJson : failed
                    ? "{\"code\":500,\"error\":\"Sensitive request: " + Token + "\"}"
                    : "{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
            };
            if (!validation && _submissions == 1 && RateLimitFirstSubmission)
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset-In", "1");
            }
            return response;
        }
    }
}
