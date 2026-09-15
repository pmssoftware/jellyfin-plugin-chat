using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Chat.Models;

public sealed class ChatChannel
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Kind { get; set; } = ChatValues.ChatKind;

    public int SortOrder { get; set; }

    public bool IsArchived { get; set; }

    public bool IsDefault { get; set; }

    public bool IsPrivate { get; set; }

    public bool IsRestricted { get; set; }

    public List<Guid> MemberUserIds { get; set; } = new();

    public string DirectPairKey { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public ChatChannel Copy() => new()
    {
        Id = Id, Name = Name, Description = Description, Kind = Kind, SortOrder = SortOrder,
        IsArchived = IsArchived, IsDefault = IsDefault, IsPrivate = IsPrivate,
        IsRestricted = IsRestricted, MemberUserIds = new List<Guid>(MemberUserIds ?? new()),
        DirectPairKey = DirectPairKey, CreatedAtUtc = CreatedAtUtc
    };
}
