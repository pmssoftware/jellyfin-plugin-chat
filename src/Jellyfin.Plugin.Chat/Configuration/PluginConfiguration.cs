using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Chat.Configuration;

/// <summary>Settings that belong in Jellyfin's normal plugin configuration.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool ChatEnabled { get; set; } = true;

    public string TabName { get; set; } = "Chat";

    public int MessageLimit { get; set; } = 1000;

    public int MinimumSecondsBetweenMessages { get; set; } = 2;

    public int RetentionDays { get; set; } = 365;
}
