using System;

namespace Jellyfin.Plugin.Chat.Models;

public sealed class BlockedUser
{
    public Guid OwnerUserId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public DateTime BlockedAtUtc { get; set; }
}
