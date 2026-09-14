using System.Collections.Generic;

namespace Jellyfin.Plugin.Chat.Models;

public sealed class ChatState
{
    public int SchemaVersion { get; set; } = 1;

    public List<ChatChannel> Channels { get; set; } = new();

    public List<ChatMessage> Messages { get; set; } = new();

    public List<MutedUser> MutedUsers { get; set; } = new();

    public List<ChatUserAccessRule> UserAccessRules { get; set; } = new();
}
