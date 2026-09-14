using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.Chat.Models;

public static class CryptoEventKinds
{
    public const string Commit = "Commit";
    public const string Welcome = "Welcome";
    public const string Application = "Application";
    public const string Delete = "Delete";
}

public sealed class ChatCryptoDevice
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime LastSeenAtUtc { get; set; }

    public bool IsRevoked { get; set; }

    public ChatCryptoDevice Copy() => (ChatCryptoDevice)MemberwiseClone();
}

public sealed class ChatCryptoKeyPackage
{
    public Guid Id { get; set; }

    public Guid ChannelId { get; set; }

    public Guid DeviceId { get; set; }

    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    public bool IsConsumed { get; set; }

    public ChatCryptoKeyPackage Copy() => (ChatCryptoKeyPackage)MemberwiseClone();
}

public sealed class ChatCryptoGroup
{
    public Guid ChannelId { get; set; }

    public long Epoch { get; set; }

    public long NextSequence { get; set; } = 1;

    public List<Guid> MemberDeviceIds { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public ChatCryptoGroup Copy() => new()
    {
        ChannelId = ChannelId,
        Epoch = Epoch,
        NextSequence = NextSequence,
        MemberDeviceIds = new List<Guid>(MemberDeviceIds),
        CreatedAtUtc = CreatedAtUtc,
        UpdatedAtUtc = UpdatedAtUtc
    };
}

public sealed class ChatCryptoEvent
{
    public Guid Id { get; set; }

    public Guid ChannelId { get; set; }

    public long Sequence { get; set; }

    public long Epoch { get; set; }

    public string Kind { get; set; } = string.Empty;

    public Guid SenderDeviceId { get; set; }

    public Guid? RecipientDeviceId { get; set; }

    public Guid? AuthorId { get; set; }

    public string AuthorName { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public Guid? TargetEventId { get; set; }

    public List<Guid> AudienceDeviceIds { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; }

    public ChatCryptoEvent Copy() => new()
    {
        Id = Id,
        ChannelId = ChannelId,
        Sequence = Sequence,
        Epoch = Epoch,
        Kind = Kind,
        SenderDeviceId = SenderDeviceId,
        RecipientDeviceId = RecipientDeviceId,
        AuthorId = AuthorId,
        AuthorName = AuthorName,
        Payload = Payload,
        TargetEventId = TargetEventId,
        AudienceDeviceIds = new List<Guid>(AudienceDeviceIds),
        CreatedAtUtc = CreatedAtUtc
    };
}

public sealed class RegisterCryptoDeviceRequest
{
    public Guid DeviceId { get; set; }

    [StringLength(80)]
    public string Label { get; set; } = string.Empty;
}

public sealed class PublishKeyPackageRequest
{
    public Guid DeviceId { get; set; }

    [Required]
    [StringLength(131072, MinimumLength = 1)]
    public string Payload { get; set; } = string.Empty;
}

public sealed class ClaimCryptoGroupRequest
{
    public Guid DeviceId { get; set; }
}

public sealed class CryptoWelcomeEnvelope
{
    public Guid RecipientDeviceId { get; set; }

    [Required]
    [StringLength(524288, MinimumLength = 1)]
    public string Payload { get; set; } = string.Empty;
}

public sealed class CommitCryptoGroupRequest
{
    public Guid EventId { get; set; }

    public Guid DeviceId { get; set; }

    public long ExpectedEpoch { get; set; }

    [Required]
    [StringLength(524288, MinimumLength = 1)]
    public string Payload { get; set; } = string.Empty;

    public List<Guid> MemberDeviceIds { get; set; } = new();

    public List<CryptoWelcomeEnvelope> Welcomes { get; set; } = new();
}

public sealed class CreateEncryptedMessageRequest
{
    public Guid EventId { get; set; }

    public Guid DeviceId { get; set; }

    public long Epoch { get; set; }

    [Required]
    [StringLength(131072, MinimumLength = 1)]
    public string Payload { get; set; } = string.Empty;
}

public sealed class CryptoDeviceInfo
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public bool IsCurrentUser { get; set; }
}

public sealed class CryptoKeyPackageInfo
{
    public Guid DeviceId { get; set; }

    public string Payload { get; set; } = string.Empty;
}

public sealed class CryptoChannelBootstrap
{
    public bool GroupExists { get; set; }

    public long Epoch { get; set; }

    public DateTime RetentionCutoffUtc { get; set; }

    public IReadOnlyList<Guid> MemberDeviceIds { get; set; } = Array.Empty<Guid>();

    public IReadOnlyList<Guid> TargetMemberDeviceIds { get; set; } = Array.Empty<Guid>();

    public IReadOnlyList<CryptoDeviceInfo> Devices { get; set; } = Array.Empty<CryptoDeviceInfo>();

    public IReadOnlyList<CryptoKeyPackageInfo> KeyPackages { get; set; } = Array.Empty<CryptoKeyPackageInfo>();

    public IReadOnlyList<CryptoKeyPackageInfo> PendingKeyPackages { get; set; } = Array.Empty<CryptoKeyPackageInfo>();

    public IReadOnlyList<ChatCryptoEvent> Events { get; set; } = Array.Empty<ChatCryptoEvent>();
}
