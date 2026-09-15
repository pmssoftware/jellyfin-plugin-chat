using System;

namespace Jellyfin.Plugin.Chat.Models;

public static class ChatValues
{
    public const string DirectKind = "Direct";
    public const string RestrictedChatKind = "RestrictedChat";
    public const string RestrictedAnnouncementKind = "RestrictedAnnouncement";
    public const string ChatKind = "Chat";
    public const string AnnouncementKind = "Announcement";

    public static string? CanonicalKind(string? value)
    {
        if (string.Equals(value, ChatKind, StringComparison.OrdinalIgnoreCase))
        {
            return ChatKind;
        }

        if (string.Equals(value, RestrictedChatKind, StringComparison.OrdinalIgnoreCase)) return RestrictedChatKind;
        if (string.Equals(value, RestrictedAnnouncementKind, StringComparison.OrdinalIgnoreCase)) return RestrictedAnnouncementKind;

        return string.Equals(value, AnnouncementKind, StringComparison.OrdinalIgnoreCase)
            ? AnnouncementKind
            : null;
    }
}
