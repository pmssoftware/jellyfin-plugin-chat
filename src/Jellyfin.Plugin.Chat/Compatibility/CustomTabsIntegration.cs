using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Jellyfin.Plugin.Chat.Compatibility;

/// <summary>Manages only Jellyfin Chat's entry in CustomTabs.</summary>
public static class CustomTabsIntegration
{
    private const string CustomTabsTypeName = "Jellyfin.Plugin.CustomTabs.CustomTabsPlugin";
    private const string ContentMarker = "JellyfinChat/App";
    private const string TemplateResource = "Jellyfin.Plugin.Chat.Web.customtabs.html";

    public static bool EnsureTab(string title)
    {
        if (!TryGetConfiguration(out var plugin, out var configuration, out var tabsProperty, out var tabs))
        {
            return false;
        }

        var existing = tabs.Cast<object?>().FirstOrDefault(IsChatTab);
        if (existing is not null)
        {
            existing.GetType().GetProperty("Title")?.SetValue(existing, title);
        }
        else
        {
            var tabType = tabsProperty.PropertyType.GetElementType();
            if (tabType is null || Activator.CreateInstance(tabType) is not object newTab)
            {
                return false;
            }

            newTab.GetType().GetProperty("Title")?.SetValue(newTab, title);
            newTab.GetType().GetProperty("ContentHtml")?.SetValue(newTab, ReadTemplate());
            existing = newTab;
        }

        // Keep Content Requests before Chat regardless of plugin startup order.
        var ordered = tabs.Cast<object?>().Where(tab => !IsChatTab(tab)).ToList();
        var requestsIndex = ordered.FindIndex(IsContentRequestsTab);
        ordered.Insert(requestsIndex >= 0 ? requestsIndex + 1 : ordered.Count, existing);
        var tabElementType = tabsProperty.PropertyType.GetElementType();
        if (tabElementType is null)
        {
            return false;
        }

        var reorderedTabs = Array.CreateInstance(tabElementType, ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            reorderedTabs.SetValue(ordered[index], index);
        }

        tabsProperty.SetValue(configuration, reorderedTabs);

        Save(plugin);
        return true;
    }

    public static bool RemoveTab()
    {
        if (!TryGetConfiguration(out var plugin, out var configuration, out var tabsProperty, out var tabs))
        {
            return false;
        }

        var remaining = tabs.Cast<object?>().Where(tab => !IsChatTab(tab)).ToArray();
        if (remaining.Length == tabs.Length)
        {
            return true;
        }

        var tabType = tabsProperty.PropertyType.GetElementType();
        if (tabType is null)
        {
            return false;
        }

        var updatedTabs = Array.CreateInstance(tabType, remaining.Length);
        for (var index = 0; index < remaining.Length; index++)
        {
            updatedTabs.SetValue(remaining[index], index);
        }

        tabsProperty.SetValue(configuration, updatedTabs);
        Save(plugin);
        return true;
    }

    private static bool TryGetConfiguration(out object plugin, out object configuration, out PropertyInfo tabsProperty, out Array tabs)
    {
        plugin = null!;
        configuration = null!;
        tabsProperty = null!;
        tabs = null!;
        var assembly = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
            .FirstOrDefault(candidate => candidate.GetType(CustomTabsTypeName) is not null);
        var pluginType = assembly?.GetType(CustomTabsTypeName);
        plugin = pluginType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)!;
        if (plugin is null)
        {
            return false;
        }

        configuration = plugin.GetType().GetProperty("Configuration")?.GetValue(plugin)!;
        tabsProperty = configuration?.GetType().GetProperty("Tabs")!;
        tabs = tabsProperty?.GetValue(configuration) as Array ?? Array.Empty<object>();
        return configuration is not null && tabsProperty is not null;
    }

    private static bool IsChatTab(object? tab)
    {
        var html = tab?.GetType().GetProperty("ContentHtml")?.GetValue(tab) as string;
        return html?.Contains(ContentMarker, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsContentRequestsTab(object? tab)
    {
        var html = tab?.GetType().GetProperty("ContentHtml")?.GetValue(tab) as string;
        return html?.Contains("ContentRequests/Form", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string ReadTemplate()
    {
        using var stream = typeof(CustomTabsIntegration).Assembly.GetManifestResourceStream(TemplateResource)
            ?? throw new InvalidOperationException("The embedded CustomTabs template is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Save(object plugin)
    {
        var saveMethod = plugin.GetType().GetMethod("SaveConfiguration", Type.EmptyTypes)
            ?? throw new MissingMethodException(plugin.GetType().FullName, "SaveConfiguration");
        saveMethod.Invoke(plugin, null);
    }
}
