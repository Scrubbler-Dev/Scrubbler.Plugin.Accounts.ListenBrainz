using Scrubbler.PluginBase.Settings;

namespace Scrubbler.Plugin.Accounts.ListenBrainz;

internal sealed class PluginSettings : IPluginSettings
{
    public bool IsScrobblingEnabled { get; set; }
}
