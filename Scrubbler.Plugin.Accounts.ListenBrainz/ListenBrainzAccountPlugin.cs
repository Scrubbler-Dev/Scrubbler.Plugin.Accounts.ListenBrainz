using System.Net;
using System.Text.Json;
using MetaBrainz.Common;
using MetaBrainz.ListenBrainz.Interfaces;
using MetaBrainz.ListenBrainz.Objects;
using Scrubbler.Abstractions;
using Scrubbler.PluginBase;
using Scrubbler.PluginBase.Plugin;
using Scrubbler.PluginBase.Plugin.Account;
using Scrubbler.PluginBase.Services;
using Scrubbler.PluginBase.Settings;
using ListenBrainzClient = MetaBrainz.ListenBrainz.ListenBrainz;

namespace Scrubbler.Plugin.Accounts.ListenBrainz;

[PluginMetadata(Name = "ListenBrainz", Description = "Scrobble to a ListenBrainz account", SupportedPlatforms = PlatformSupport.All)]
public sealed partial class ListenBrainzAccountPlugin : PluginBase.Plugin.PluginBase, IAccountPlugin, IDisposable,
    ICanUpdateNowPlaying, ICanLoveTracks, ICanFetchPlayCounts, ICanFetchTags, ICanOpenLinks
{
    private const string CredentialsKey = "ListenBrainzCredentials";
    private readonly ListenBrainzClient _client;
    private readonly ISecureStore _secureStore;
    private readonly ISettingsStore _settingsStore;
    private readonly Func<Func<string, Task<string?>>, Task> _showLoginDialog;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PluginSettings _settings = new();

    public string? AccountId { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrEmpty(AccountId) && !string.IsNullOrEmpty(_client.UserToken);
    public event EventHandler? IsScrobblingEnabledChanged;

    public bool IsScrobblingEnabled
    {
        get => _settings.IsScrobblingEnabled;
        set
        {
            if (_settings.IsScrobblingEnabled == value) return;
            _settings.IsScrobblingEnabled = value;
            IsScrobblingEnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrubbler", "Plugins", "ListenBrainz");

    public ListenBrainzAccountPlugin(IDialogService dialogs, ILinkOpenerService linkOpener, IModuleLogServiceFactory logFactory)
        : this(logFactory,
            new ListenBrainzClient("Scrubbler", typeof(ListenBrainzAccountPlugin).Assembly.GetName().Version,
                "https://github.com/Scrubbler-Dev"),
            new FileSecureStore(Path.Combine(SettingsDirectory, "settings.dat"), "ListenBrainz"),
            new JsonSettingsStore(Path.Combine(SettingsDirectory, "settings.json")),
            new TokenLoginDialog(dialogs, linkOpener).ShowAsync, linkOpener: linkOpener)
    { }

    internal ListenBrainzAccountPlugin(IModuleLogServiceFactory logFactory, ListenBrainzClient client,
        ISecureStore secureStore, ISettingsStore settingsStore, Func<Func<string, Task<string?>>, Task> showLoginDialog,
        ListenBrainzMetadataApi? metadataApi = null, ILinkOpenerService? linkOpener = null)
        : base(logFactory)
    {
        _client = client;
        _secureStore = secureStore;
        _settingsStore = settingsStore;
        _showLoginDialog = showLoginDialog;
        _metadataApi = metadataApi ?? new ListenBrainzMetadataApi(Version);
        _linkOpener = linkOpener;
    }

    public async Task LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _settings = await _settingsStore.GetOrCreateAsync<PluginSettings>(Name);
            AccountId = null;
            _client.UserToken = null;
            ClearAccountFunctionCaches();
            var json = await _secureStore.GetAsync(CredentialsKey);
            Credentials? credentials;
            try { credentials = json == null ? null : JsonSerializer.Deserialize<Credentials>(json); }
            catch (JsonException)
            {
                _logService.Warn("Saved ListenBrainz credentials could not be read. Please reconnect your account.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(credentials?.AccountId) && !string.IsNullOrWhiteSpace(credentials.Token))
            {
                _client.UserToken = credentials.Token;
                AccountId = credentials.AccountId;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync()
    {
        await _gate.WaitAsync();
        try { await _settingsStore.SetAsync(Name, _settings); }
        finally { _gate.Release(); }
    }

    public Task AuthenticateAsync() => _showLoginDialog(LoginWithTokenAsync);

    // Return a user-facing error to keep the login dialog open on failure.
    internal async Task<string?> LoginWithTokenAsync(string token)
    {
        token = token.Trim();
        if (string.IsNullOrEmpty(token)) return "Enter your ListenBrainz user token.";
        await _gate.WaitAsync();
        try
        {
            await WaitForRateLimitAsync();
            var previousToken = _client.UserToken;
            ITokenValidationResult validation;
            try
            {
                // The API prefers the header over the query token used by this library.
                _client.UserToken = token;
                validation = await _client.ValidateTokenAsync(token);
            }
            finally { _client.UserToken = previousToken; }
            if (validation.Valid != true || string.IsNullOrWhiteSpace(validation.User))
                return "The token is invalid. Copy your user token from ListenBrainz settings and try again.";

            // Store token and identity together, before exposing an authenticated account.
            await _secureStore.SaveAsync(CredentialsKey, JsonSerializer.Serialize(new Credentials(validation.User, token)));
            _client.UserToken = token;
            AccountId = validation.User;
            ClearAccountFunctionCaches();
            _logService.Info("Connected to ListenBrainz.");
            return null;
        }
        catch (Exception ex)
        {
            // Library exceptions may contain the token in a request URL or headers.
            _logService.Warn($"ListenBrainz login failed ({ex.GetType().Name}).");
            return DescribeError(ex);
        }
        finally { _gate.Release(); }
    }

    public async Task LogoutAsync()
    {
        await _gate.WaitAsync();
        try { await _secureStore.RemoveAsync(CredentialsKey); }
        finally
        {
            _client.UserToken = null;
            AccountId = null;
            ClearAccountFunctionCaches();
            _gate.Release();
        }
    }

    public async Task<ScrobbleResponse> ScrobbleAsync(IEnumerable<ScrobbleData> scrobbles)
    {
        await _gate.WaitAsync();
        var accepted = 0;
        try
        {
            if (!IsAuthenticated) return new(false, "Not connected to ListenBrainz. Connect your account first.");
            if (!IsScrobblingEnabled) return new(false, "Scrobbling to ListenBrainz is disabled.");

            // Materialize and validate everything before submitting any batch.
            var listens = scrobbles.Select(ToListen).ToArray();
            foreach (var batch in listens.Chunk(ListenBrainzClient.MaxListensPerRequest))
            {
                await WaitForRateLimitAsync();
                // Scrubbler's inputs can be historical, even when there is only one track.
                await _client.ImportListensAsync(batch);
                accepted += batch.Length;
                _statistics.Clear();
                _logService.Info($"Submitted {accepted} / {listens.Length} listens to ListenBrainz.");
            }
            return new(true, null);
        }
        catch (Exception ex)
        {
            _logService.Warn($"ListenBrainz submission failed ({ex.GetType().Name}); {accepted} listens confirmed.");
            return new(false, $"{DescribeError(ex)} At least {accepted} listens confirmed before the failure. " +
                "The failed request may have been accepted; check ListenBrainz before resubmitting.");
        }
        finally { _gate.Release(); }
    }

    private SubmittedListen ToListen(ScrobbleData scrobble)
    {
        if (string.IsNullOrWhiteSpace(scrobble.Artist) || string.IsNullOrWhiteSpace(scrobble.Track))
            throw new ArgumentException("Artist and track name are required.");
        var additionalInfo = new Dictionary<string, object?>
        {
            ["submission_client"] = "Scrubbler",
            ["submission_client_version"] = Version.ToString()
        };
        if (!string.IsNullOrWhiteSpace(scrobble.AlbumArtist)) additionalInfo["albumartist"] = scrobble.AlbumArtist;
        return new SubmittedListen
        {
            Timestamp = scrobble.Timestamp,
            Track = new SubmittedTrackInfo
            {
                Artist = scrobble.Artist,
                Name = scrobble.Track,
                Release = string.IsNullOrWhiteSpace(scrobble.Album) ? null : scrobble.Album,
                AdditionalInfo = additionalInfo
            }
        };
    }

    private async Task WaitForRateLimitAsync()
    {
        var rate = _metadataApi.RateLimitInfo.LastRequest > _client.RateLimitInfo.LastRequest
            ? _metadataApi.RateLimitInfo : _client.RateLimitInfo;
        if (rate.RemainingRequests != 0) return;
        var reset = rate.ResetIn is int seconds ? rate.LastRequest.AddSeconds(seconds) : rate.ResetAt;
        if (reset is not null)
        {
            var delay = reset.Value - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
        }
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        AccountFunctionException error => error.Message,
        HttpError { Status: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            "ListenBrainz rejected the token. Log out and reconnect with a valid user token.",
        HttpError { Status: HttpStatusCode.TooManyRequests } =>
            "ListenBrainz is rate limiting requests. Please try again later.",
        HttpError error => $"ListenBrainz returned HTTP {(int)error.Status}. Please try again later.",
        HttpRequestException => "Could not reach ListenBrainz. Check your connection and try again.",
        OperationCanceledException => "The ListenBrainz request timed out. Please try again later.",
        ArgumentException => "One or more values are invalid. Check the token or track information.",
        IOException or UnauthorizedAccessException => "Could not access saved ListenBrainz settings. Check file permissions.",
        _ => "The ListenBrainz operation failed. Please try again."
    };

    public override IPluginViewModel GetViewModel() => new AccountViewModel();
    private sealed class AccountViewModel : PluginViewModelBase { }
    private sealed record Credentials(string AccountId, string Token);

    public void Dispose()
    {
        _client.Dispose();
        _metadataApi.Dispose();
        _gate.Dispose();
    }
}
