# ListenBrainz account plugin for Scrubbler

Connect a ListenBrainz account and submit tracks from Scrubbler's existing scrobbling plugins.
Uses [MetaBrainz.ListenBrainz 6.0.0](https://www.nuget.org/packages/MetaBrainz.ListenBrainz/6.0.0).

## Usage

1. Open **Accounts** in Scrubbler and authenticate the **ListenBrainz** account.
2. Follow **Open ListenBrainz settings**, copy your user token, and paste it into the dialog.
3. Select **Connect**, then enable scrobbling for the account.
4. Submit tracks using any existing scrobbling plugin. If Last.fm is also enabled, both accounts receive them.

Authentication is saved immediately using Scrubbler's existing `FileSecureStore`. Logout removes
the saved credentials immediately. Settings live under the application-data directory at
`Scrubbler/Plugins/ListenBrainz`. The enablement setting is saved through the host's normal persistence lifecycle.
Stored authentication is restored without a network request so startup also works offline.
If a token is revoked, log out and reconnect with a new token.

Submissions use ListenBrainz's `import` mode, including for a single historical track. Artist,
track, album, album artist, and the original UTC timestamp are preserved. Requests are batched,
and the plugin waits for a reported rate-limit reset before the next batch. Failed requests are
not retried automatically. A failure reports confirmed progress; check your ListenBrainz history
before resubmitting because a request can succeed remotely even if its response is lost.

This first version provides login, logout, and scrobbling. It does not advertise now playing,
love/unlove, tags, play counts, or a Last.fm-style daily scrobble limit.

## Development

Requires the .NET 10 SDK. Initialize the shared dependency submodule after cloning:

```sh
git submodule update --init --recursive
dotnet test -c Release
dotnet build Scrubbler.Plugin.Accounts.ListenBrainz/Scrubbler.Plugin.Accounts.ListenBrainz.csproj -c Debug
```

With this repository next to `Scrubbler-2`, the shared Debug build target copies the plugin and
its dependencies into `Scrubbler-2/Scrubbler/DebugPlugins/Scrubbler.Plugin.Accounts.ListenBrainz`.
Restart the development host to discover the account.

Tests use the real client library with an in-memory HTTP handler. They cover validation, token
replacement, persisted authentication, logout, metadata/UTC mapping, batching, rate limiting,
and failures without contacting ListenBrainz or requiring credentials. The actual dialog and
live account submission still need a manual smoke test in the desktop host.

## Publishing

The release workflow uses the existing Scrubbler-Plugins reusable publisher. Configure
`SCRUBBLER_PLUGINS_PAT` in the new repository before publishing a release. No embedded API keys,
client secrets, or `PluginDefaults` injection are needed. The workflow builds, tests, and publishes
the plugin ZIP and icon to the plugin catalog.

## Attribution

The plugin follows the existing Scrubbler account plugin's GPL-3.0 license; see `LICENSE`.
The ListenBrainz icon is the upstream
[favicon-256.png](https://github.com/metabrainz/listenbrainz-server/blob/master/frontend/img/favicon-256.png),
used to identify the service. ListenBrainz branding belongs to MetaBrainz.
MetaBrainz.ListenBrainz is MIT licensed; see `THIRD-PARTY-NOTICES.md`.
