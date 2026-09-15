using System.IO;
using System.Reflection;

namespace Jellyfin.Plugin.Chat.Compatibility;

public static class WebTransformations
{
    private const string Marker = "jellyfinChatTabBridge";

    public static string IndexHtml(PatchRequestPayload payload)
    {
        var contents = payload.Contents ?? string.Empty;
        if (contents.Contains(Marker, System.StringComparison.Ordinal))
        {
            return contents;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Chat.Web.chat-bridge.js");
        if (stream is null)
        {
            return contents;
        }

        using var reader = new StreamReader(stream);
        var bodyIndex = contents.LastIndexOf("</body>", System.StringComparison.OrdinalIgnoreCase);
        if (bodyIndex < 0)
        {
            return contents;
        }

        var script = $"<script defer>{reader.ReadToEnd()}</script>";
        return contents.Insert(bodyIndex, script);
    }
}
