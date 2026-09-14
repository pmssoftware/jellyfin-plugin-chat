using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chat.Compatibility;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chat.Services;

public sealed class StartupService : IScheduledTask
{
    private static readonly Guid TransformationId = Guid.Parse("76703f34-ddb6-455f-843d-09b5343db535");
    private readonly ILogger<StartupService> _logger;

    public StartupService(ILogger<StartupService> logger)
    {
        _logger = logger;
    }

    public string Name => "Jellyfin Chat Startup";
    public string Key => "Jellyfin.Plugin.Chat.Startup";
    public string Description => "Creates the Chat tab and registers its Jellyfin Web compatibility bridge.";
    public string Category => "Startup Services";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            var tabName = Plugin.Instance?.Store.GetSettings().TabName ?? "Chat";
            if (CustomTabsIntegration.EnsureTab(tabName))
            {
                _logger.LogInformation("Ensured the Jellyfin Chat entry exists in CustomTabs.");
            }
            else
            {
                _logger.LogWarning("CustomTabs is unavailable; the Chat entry was not created.");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not create or update the Jellyfin Chat entry in CustomTabs.");
        }

        try
        {
            var assembly = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
                .FirstOrDefault(candidate => candidate.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);
            var pluginInterface = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var registerMethod = pluginInterface?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            var payloadType = registerMethod?.GetParameters().SingleOrDefault()?.ParameterType;
            var parseMethod = payloadType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) });
            if (registerMethod is null || parseMethod is null)
            {
                _logger.LogWarning("File Transformation is unavailable; the Chat compatibility bridge was not registered.");
                return Task.CompletedTask;
            }

            var payloadJson = JsonSerializer.Serialize(new
            {
                id = TransformationId,
                fileNamePattern = "index.html",
                callbackAssembly = GetType().Assembly.FullName,
                callbackClass = typeof(WebTransformations).FullName,
                callbackMethod = nameof(WebTransformations.IndexHtml)
            });
            var payload = parseMethod.Invoke(null, new object[] { payloadJson });
            registerMethod.Invoke(null, new[] { payload });
            _logger.LogInformation("Registered the Jellyfin Chat homepage compatibility bridge.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not register the Jellyfin Chat homepage compatibility bridge.");
        }

        return Task.CompletedTask;
    }

    public static void RemoveTransformation()
    {
        var assembly = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
            .FirstOrDefault(candidate => candidate.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);
        var pluginInterface = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        pluginInterface?.GetMethod("RemoveTransformation", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, new object[] { TransformationId });
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}
