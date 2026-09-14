using System;

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

    public DateTime CreatedAtUtc { get; set; }

    public ChatChannel Copy() => (ChatChannel)MemberwiseClone();
}
