using System.Collections.Generic;

namespace Jellyfin.Plugin.Chat.Models;

public sealed class ChatState
{
    public int SchemaVersion { get; set; } = 3;

    public List<ChatChannel> Channels { get; set; } = new();

    public List<ChatMessage> Messages { get; set; } = new();

    public List<MutedUser> MutedUsers { get; set; } = new();

    public List<ChatUserAccessRule> UserAccessRules { get; set; } = new();

    public List<ChatCryptoDevice> CryptoDevices { get; set; } = new();

    public List<ChatCryptoKeyPackage> CryptoKeyPackages { get; set; } = new();

    public List<ChatCryptoGroup> CryptoGroups { get; set; } = new();

    public List<ChatCryptoEvent> CryptoEvents { get; set; } = new();
}
