using System;

namespace Jellyfin.Plugin.Chat.Models;

public static class ChatValues
{
    public const string ChatKind = "Chat";
    public const string AnnouncementKind = "Announcement";

    public static string? CanonicalKind(string? value)
    {
        if (string.Equals(value, ChatKind, StringComparison.OrdinalIgnoreCase))
        {
            return ChatKind;
        }

        return string.Equals(value, AnnouncementKind, StringComparison.OrdinalIgnoreCase)
            ? AnnouncementKind
            : null;
    }
}
