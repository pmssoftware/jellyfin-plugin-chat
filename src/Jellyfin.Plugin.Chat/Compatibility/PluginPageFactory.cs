using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Chat.Compatibility;

public static class PluginPageFactory
{
    public static PluginPageInfo CreateAdminPage(string pluginNamespace) => new()
    {
        Name = "jellyfin-chat",
        DisplayName = "Jellyfin Chat",
        EnableInMainMenu = true,
        MenuSection = "server",
        MenuIcon = "forum",
        EmbeddedResourcePath = $"{pluginNamespace}.Web.admin.html"
    };
}
