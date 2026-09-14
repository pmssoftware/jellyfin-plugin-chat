using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Chat.Compatibility;
using Jellyfin.Plugin.Chat.Configuration;
using Jellyfin.Plugin.Chat.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Chat;

/// <summary>Jellyfin Chat plugin entry point.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private static readonly Guid PluginId = Guid.Parse("6d952fa7-3c3f-46b1-a3f7-69dc705f2692");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        Store = new ChatStore(this);
    }

    public static Plugin? Instance { get; private set; }

    public ChatStore Store { get; }

    public override string Name => "Jellyfin Chat";

    public override string Description => "Self-contained chat and announcement channels for Jellyfin users.";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return PluginPageFactory.CreateAdminPage(GetType().Namespace!);
    }

    public override void OnUninstalling()
    {
        try
        {
            CustomTabsIntegration.RemoveTab();
        }
        catch
        {
            // A missing or changed optional dependency must not block uninstall.
        }

        try
        {
            StartupService.RemoveTransformation();
        }
        catch
        {
            // A missing File Transformation plugin must not block uninstall.
        }

        Instance = null;
        base.OnUninstalling();
    }
}
