using System;

namespace Jellyfin.Plugin.Chat.Models;

public sealed class CreateMessageRequest
{
    public Guid ChannelId { get; set; }

    public string Body { get; set; } = string.Empty;
}

public sealed class CreateChannelRequest
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Kind { get; set; } = ChatValues.ChatKind;
}

public sealed class UpdateChannelRequest
{
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Kind { get; set; } = ChatValues.ChatKind;

    public int SortOrder { get; set; }

    public bool IsArchived { get; set; }
}

public sealed class MuteUserRequest
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}

public sealed class ChatSettings
{
    public bool ChatEnabled { get; set; }

    public string TabName { get; set; } = "Chat";

    public int MessageLimit { get; set; }

    public int MinimumSecondsBetweenMessages { get; set; }

    public int RetentionDays { get; set; }
}

public sealed class ChatBootstrap
{
    public System.Collections.Generic.IReadOnlyList<ChatChannel> Channels { get; set; } = System.Array.Empty<ChatChannel>();

    public bool IsAdministrator { get; set; }

    public bool IsMuted { get; set; }

    public bool IsEnabled { get; set; }

    public string CurrentUserName { get; set; } = string.Empty;

    public Guid? CurrentUserId { get; set; }

    public string EncryptionProtocol { get; set; } = "MLS 1.0";

    public int PollIntervalMilliseconds { get; set; } = 2500;

    public int MessageLimit { get; set; }
}

public sealed class SetUserAccessRequest
{
    public bool Enabled { get; set; }
}

public sealed class ChatUserAccess
{
    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public bool IsAdministrator { get; set; }

    public DateTime? LastLoginDateUtc { get; set; }
}
