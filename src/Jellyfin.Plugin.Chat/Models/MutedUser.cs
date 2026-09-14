using System;

namespace Jellyfin.Plugin.Chat.Models;

public sealed class MutedUser
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public DateTime MutedAtUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public MutedUser Copy() => (MutedUser)MemberwiseClone();
}
