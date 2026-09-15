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

## Account functions

Select ListenBrainz as the account-functions provider in **Accounts** to use these capabilities
in compatible scrobbling plugins. The existing `AccountFunctionContainer` discovers all five
interfaces automatically; no host or PluginBase changes are required.

| Function | Behavior |
| --- | --- |
| Now playing | Sends `playing_now` without a timestamp while scrobbling is enabled. |
| Love/unlove | Resolves a recording MBID, reads your feedback, and submits score `1` or `0` (remove feedback). A dislike is not reported as loved. |
| Personal play counts | Reads all pages of your all-time artist, recording, or release statistics and sums matching names. |
| Track tags | Uses recording tags from ListenBrainz's MusicBrainz metadata lookup. |
| Artist/album tags | Uses MusicBrainz artist/release-group searches and public tags. |
| Links | Opens artist, album, and track searches on ListenBrainz, and tag pages on MusicBrainz. |

Play counts reflect ListenBrainz's periodically computed statistics, not a live counter. Names
are compared case-insensitively; matching recording/release rows are summed across releases or
editions. Statistics are cached for ten minutes and invalidated after scrobbling or changing
accounts. Unavailable or incomplete statistics produce an error rather than a misleading zero.

Metadata is cached for ten minutes. Unmatched or ambiguous names return a useful error instead
of selecting an unrelated recording or artist. Love/unlove requires a MusicBrainz recording
match; it does not submit a fake listen to obtain an identifier. Artist/album tags come from
MusicBrainz, with requests limited to one per 1.1 seconds and no ListenBrainz token sent there.
Account functions that fetch or modify data require login; opening links does not.

The plugin does not impose a Last.fm-style daily scrobble limit.

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
and account functions without contacting ListenBrainz or MusicBrainz or requiring credentials. The actual dialog and
live account submission still need a manual smoke test in the desktop host.

`ListenBrainzMetadataApi` handles metadata lookup and user-specific feedback reads that the
library does not expose. It also sends now-playing payloads directly because version 6.0.0's
`SetNowPlayingAsync` serializes CLR property names instead of the required JSON wire format.
The library remains responsible for scrobbling, feedback writes, and statistics.

## Publishing

The release workflow uses the existing Scrubbler-Plugins reusable publisher. Configure
`SCRUBBLER_PLUGINS_PAT` in the new repository before publishing a release. No embedded API keys,
client secrets, or `PluginDefaults` injection are needed. The workflow builds, tests, and publishes
the plugin ZIP and icon to the plugin catalog.
