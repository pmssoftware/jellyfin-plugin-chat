using System;

namespace Jellyfin.Plugin.Chat.Models;

/// <summary>A stored override for one Jellyfin user's chat access.</summary>
public sealed class ChatUserAccessRule
{
    public Guid UserId { get; set; }

    public bool Enabled { get; set; } = true;
}
