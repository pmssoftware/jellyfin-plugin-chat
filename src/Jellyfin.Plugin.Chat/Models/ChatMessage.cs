using System;

namespace Jellyfin.Plugin.Chat.Models;

public sealed class ChatMessage
{
    public Guid Id { get; set; }

    public Guid ChannelId { get; set; }

    public Guid? AuthorId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public ChatMessage Copy() => (ChatMessage)MemberwiseClone();
}
