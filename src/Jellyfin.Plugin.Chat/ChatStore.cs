using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Chat.Compatibility;
using Jellyfin.Plugin.Chat.Models;

namespace Jellyfin.Plugin.Chat;

/// <summary>Thread-safe, self-contained persistence for channels, messages, and moderation state.</summary>
public sealed class ChatStore
{
    private const int MaximumReturnedMessages = 200;
    private const int MaximumCryptoDevices = 512;
    private const int MaximumCryptoDevicesPerUser = 16;
    private const int MaximumCryptoPayloadLength = 524288;
    private const byte MlsPrivateMessageWireFormat = 2;
    private const byte MlsWelcomeWireFormat = 3;
    private const byte MlsKeyPackageWireFormat = 5;
    private readonly object _sync = new();
    private readonly Plugin _plugin;
    private readonly string _statePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private ChatState _state;

    public ChatStore(Plugin plugin)
    {
        _plugin = plugin;
        Directory.CreateDirectory(plugin.DataFolderPath);
        _statePath = Path.Combine(plugin.DataFolderPath, "chat-state.json");
        _state = Load();
        EnsureDefaults();
        Save();
    }

    public IReadOnlyList<ChatChannel> GetChannels(bool includeArchived)
    {
        lock (_sync)
        {
            return _state.Channels
                .Where(channel => includeArchived || !channel.IsArchived)
                .OrderBy(channel => channel.SortOrder)
                .ThenBy(channel => channel.CreatedAtUtc)
                .Select(channel => channel.Copy())
                .ToList();
        }
    }

    public IReadOnlyList<ChatChannel> GetChannelsForUser(bool includeArchived, Guid userId)
    {
        lock (_sync)
        {
            return _state.Channels
                .Where(channel => (includeArchived || !channel.IsArchived) && CanAccessChannelUnsafe(channel, userId))
                .OrderBy(channel => channel.SortOrder).ThenBy(channel => channel.CreatedAtUtc)
                .Select(channel => channel.Copy()).ToList();
        }
    }

    public IReadOnlyList<PrivateChatUser> GetPrivateChatUsers(Guid currentUserId, IEnumerable<PrivateChatUser> users)
    {
        lock (_sync)
        {
            return users.Where(user => user.UserId != currentUserId && IsUserEnabledUnsafe(user.UserId))
                .OrderBy(user => user.UserName).ToList();
        }
    }

    public ChatChannel AddOrGetPrivateChannel(Guid currentUserId, Guid otherUserId, string otherUserName)
    {
        lock (_sync)
        {
            if (currentUserId == Guid.Empty || otherUserId == Guid.Empty || currentUserId == otherUserId)
                throw new InvalidOperationException("Choose another Jellyfin user.");
            if (!IsUserEnabledUnsafe(currentUserId) || !IsUserEnabledUnsafe(otherUserId))
                throw new UnauthorizedAccessException("Private chat is not enabled for this user.");
            var ids = new[] { currentUserId, otherUserId }.OrderBy(id => id).ToList();
            var pairKey = string.Join(":", ids);
            var existing = _state.Channels.FirstOrDefault(channel => channel.IsPrivate && channel.DirectPairKey == pairKey && !channel.IsArchived);
            if (existing is not null) return existing.Copy();
            var channel = new ChatChannel
            {
                Id = Guid.NewGuid(), Name = otherUserName, Description = "Private conversation",
                Kind = ChatValues.DirectKind, IsPrivate = true, IsRestricted = true,
                MemberUserIds = ids, DirectPairKey = pairKey,
                SortOrder = _state.Channels.Count == 0 ? 0 : _state.Channels.Max(item => item.SortOrder) + 1,
                CreatedAtUtc = DateTime.UtcNow
            };
            _state.Channels.Add(channel); SaveUnsafe(); return channel.Copy();
        }
    }

    public IReadOnlyList<ChatMessage> GetMessages(Guid channelId, DateTime? afterUtc, int limit)
    {
        lock (_sync)
        {
            if (!_state.Channels.Any(channel => channel.Id == channelId && !channel.IsArchived))
            {
                return Array.Empty<ChatMessage>();
            }

            if (PruneExpiredMessagesUnsafe())
            {
                SaveUnsafe();
            }
            var safeLimit = Math.Clamp(limit, 1, MaximumReturnedMessages);
            var query = _state.Messages.Where(message => message.ChannelId == channelId);
            if (afterUtc.HasValue)
            {
                query = query.Where(message => message.CreatedAtUtc > afterUtc.Value.ToUniversalTime());
                return query.OrderBy(message => message.CreatedAtUtc)
                    .Take(safeLimit)
                    .Select(message => message.Copy())
                    .ToList();
            }

            return query.OrderByDescending(message => message.CreatedAtUtc)
                .Take(safeLimit)
                .OrderBy(message => message.CreatedAtUtc)
                .Select(message => message.Copy())
                .ToList();
        }
    }

    public bool CanAccessChannel(Guid channelId, Guid userId)
    {
        lock (_sync) return _state.Channels.FirstOrDefault(channel => channel.Id == channelId && !channel.IsArchived) is { } channel
            && CanAccessChannelUnsafe(channel, userId);
    }

    public ChatMessage AddMessage(Guid channelId, Guid? authorId, string authorName, string body, bool isAdministrator)
    {
        lock (_sync)
        {
            var channel = RequireAccessibleChannelUnsafe(channelId, authorId ?? Guid.Empty);
            if ((channel.Kind == ChatValues.AnnouncementKind || channel.Kind == ChatValues.RestrictedAnnouncementKind) && !isAdministrator)
            {
                throw new UnauthorizedAccessException("Only administrators can post in announcement channels.");
            }

            if (authorId.HasValue && _state.MutedUsers.Any(mute => mute.UserId == authorId.Value) && !isAdministrator)
            {
                throw new UnauthorizedAccessException("You are muted from chat.");
            }

            var now = DateTime.UtcNow;
            var minimumInterval = TimeSpan.FromSeconds(Math.Clamp(_plugin.Configuration.MinimumSecondsBetweenMessages, 0, 300));
            var lastMessage = _state.Messages
                .Where(message => authorId.HasValue && message.AuthorId == authorId)
                .OrderByDescending(message => message.CreatedAtUtc)
                .FirstOrDefault();
            if (!isAdministrator && lastMessage is not null && now - lastMessage.CreatedAtUtc < minimumInterval)
            {
                throw new InvalidOperationException("Please wait before sending another message.");
            }

            var message = new ChatMessage
            {
                Id = Guid.NewGuid(),
                ChannelId = channelId,
                AuthorId = authorId,
                AuthorName = authorName,
                Body = body,
                CreatedAtUtc = now
            };
            _state.Messages.Add(message);
            PruneExpiredMessagesUnsafe();
            SaveUnsafe();
            return message.Copy();
        }
    }

    public ChatCryptoDevice RegisterCryptoDevice(Guid userId, string userName, RegisterCryptoDeviceRequest input)
    {
        lock (_sync)
        {
            if (input.DeviceId == Guid.Empty)
            {
                throw new InvalidOperationException("A valid device identifier is required.");
            }

            var existing = _state.CryptoDevices.FirstOrDefault(device => device.Id == input.DeviceId);
            if (existing is not null && existing.UserId != userId)
            {
                throw new UnauthorizedAccessException("This encrypted-chat device belongs to another user.");
            }

            if (existing?.IsRevoked == true)
            {
                throw new UnauthorizedAccessException("This encrypted-chat device has been revoked.");
            }

            var now = DateTime.UtcNow;
            if (existing is null)
            {
                if (_state.CryptoDevices.Count(device => !device.IsRevoked && device.UserId == userId) >= MaximumCryptoDevicesPerUser)
                {
                    throw new InvalidOperationException("This user has reached the encrypted-chat device limit.");
                }

                if (_state.CryptoDevices.Count(device => !device.IsRevoked) >= MaximumCryptoDevices)
                {
                    throw new InvalidOperationException("The encrypted-chat device limit has been reached.");
                }

                existing = new ChatCryptoDevice
                {
                    Id = input.DeviceId,
                    UserId = userId,
                    CreatedAtUtc = now
                };
                _state.CryptoDevices.Add(existing);
            }

            existing.UserName = userName;
            existing.Label = string.IsNullOrWhiteSpace(input.Label) ? "Web browser" : input.Label.Trim();
            existing.LastSeenAtUtc = now;
            SaveUnsafe();
            return existing.Copy();
        }
    }

    public IReadOnlyList<ChatCryptoDevice> GetCryptoDevices()
    {
        lock (_sync)
        {
            return _state.CryptoDevices
                .Where(device => !device.IsRevoked)
                .OrderBy(device => device.UserName)
                .ThenByDescending(device => device.LastSeenAtUtc)
                .Select(device => device.Copy())
                .ToList();
        }
    }

    public bool RevokeCryptoDevice(Guid deviceId)
    {
        lock (_sync)
        {
            var device = _state.CryptoDevices.FirstOrDefault(candidate => candidate.Id == deviceId && !candidate.IsRevoked);
            if (device is null)
            {
                return false;
            }

            device.IsRevoked = true;
            _state.CryptoKeyPackages.RemoveAll(package => package.DeviceId == deviceId);
            SaveUnsafe();
            return true;
        }
    }

    public ChatCryptoKeyPackage PublishCryptoKeyPackage(
        Guid channelId,
        Guid userId,
        PublishKeyPackageRequest input)
    {
        lock (_sync)
        {
            var channel = RequireAccessibleChannelUnsafe(channelId, userId);
            RequireOwnedDeviceUnsafe(input.DeviceId, userId);
            if (!IsUserEnabledUnsafe(userId))
            {
                throw new UnauthorizedAccessException("Chat is not enabled for this user.");
            }

            var group = _state.CryptoGroups.FirstOrDefault(candidate => candidate.ChannelId == channelId);
            if (group?.MemberDeviceIds.Contains(input.DeviceId) == true)
            {
                return _state.CryptoKeyPackages
                    .Where(package => package.ChannelId == channelId && package.DeviceId == input.DeviceId && package.IsConsumed)
                    .OrderByDescending(package => package.CreatedAtUtc)
                    .First()
                    .Copy();
            }

            ValidateCryptoPayload(input.Payload, 131072, MlsKeyPackageWireFormat);
            var existingPending = _state.CryptoKeyPackages.FirstOrDefault(package =>
                package.ChannelId == channelId
                && package.DeviceId == input.DeviceId
                && !package.IsConsumed);
            if (existingPending is not null && existingPending.Payload == input.Payload)
            {
                return existingPending.Copy();
            }

            _state.CryptoKeyPackages.RemoveAll(package =>
                package.ChannelId == channelId && package.DeviceId == input.DeviceId && !package.IsConsumed);
            var package = new ChatCryptoKeyPackage
            {
                Id = Guid.NewGuid(),
                ChannelId = channelId,
                DeviceId = input.DeviceId,
                Payload = input.Payload,
                CreatedAtUtc = DateTime.UtcNow
            };
            _state.CryptoKeyPackages.Add(package);
            SaveUnsafe();
            return package.Copy();
        }
    }

    public CryptoChannelBootstrap GetCryptoBootstrap(
        Guid channelId,
        Guid deviceId,
        Guid userId,
        long afterSequence,
        int limit)
    {
        lock (_sync)
        {
            var channel = RequireAccessibleChannelUnsafe(channelId, userId);
            var currentDevice = RequireOwnedDeviceUnsafe(deviceId, userId);
            if (!IsUserEnabledUnsafe(userId))
            {
                throw new UnauthorizedAccessException("Chat is not enabled for this user.");
            }

            var changed = PruneExpiredCryptoEventsUnsafe();
            if (ResetOrphanedCryptoGroupUnsafe(channelId))
            {
                changed = true;
            }
            var now = DateTime.UtcNow;
            if (now - currentDevice.LastSeenAtUtc >= TimeSpan.FromMinutes(5))
            {
                currentDevice.LastSeenAtUtc = now;
                changed = true;
            }

            var group = _state.CryptoGroups.FirstOrDefault(candidate => candidate.ChannelId == channelId);
            var target = TargetCryptoMembersUnsafe(channelId).OrderBy(id => id).ToList();
            var members = group?.MemberDeviceIds.Distinct().OrderBy(id => id).ToList() ?? new List<Guid>();
            var joinedUsers = _state.CryptoDevices.Where(device => members.Contains(device.Id) && !device.IsRevoked).Select(device => device.UserId).ToHashSet();
            var visibleDeviceIds = members.Concat(target).Append(deviceId).Distinct().ToHashSet();
            var devices = _state.CryptoDevices
                .Where(device => visibleDeviceIds.Contains(device.Id))
                .OrderBy(device => device.UserName)
                .ThenBy(device => device.CreatedAtUtc)
                .Select(device => new CryptoDeviceInfo
                {
                    Id = device.Id,
                    UserId = device.UserId,
                    UserName = device.UserName,
                    Label = device.Label,
                    IsCurrentUser = device.UserId == userId
                })
                .ToList();
            var pending = _state.CryptoKeyPackages
                .Where(package => package.ChannelId == channelId && !package.IsConsumed && target.Contains(package.DeviceId))
                .OrderBy(package => package.CreatedAtUtc)
                .Select(package => new CryptoKeyPackageInfo { DeviceId = package.DeviceId, Payload = package.Payload })
                .ToList();
            var keyPackages = _state.CryptoKeyPackages
                .Where(package => package.ChannelId == channelId && visibleDeviceIds.Contains(package.DeviceId))
                .GroupBy(package => package.DeviceId)
                .Select(grouping => grouping.OrderByDescending(package => package.CreatedAtUtc).First())
                .Select(package => new CryptoKeyPackageInfo { DeviceId = package.DeviceId, Payload = package.Payload })
                .ToList();
            var safeLimit = Math.Clamp(limit, 1, MaximumReturnedMessages);
            var events = _state.CryptoEvents
                .Where(item => item.ChannelId == channelId
                    && item.Sequence > Math.Max(0, afterSequence)
                    && item.AudienceDeviceIds.Contains(deviceId))
                .OrderBy(item => item.Sequence)
                .Take(safeLimit)
                .Select(item => item.Copy())
                .ToList();
            if (changed)
            {
                SaveUnsafe();
            }

            return new CryptoChannelBootstrap
            {
                GroupExists = group is not null,
                Epoch = group?.Epoch ?? 0,
                RetentionCutoffUtc = DateTime.UtcNow.AddDays(-Math.Clamp(_plugin.Configuration.RetentionDays, 1, 3650)),
                MemberDeviceIds = members,
                TargetMemberDeviceIds = target,
                Devices = devices,
                KeyPackages = keyPackages,
                PendingKeyPackages = pending,
                Events = events,
                AllParticipantsReady = (channel.IsPrivate || channel.IsRestricted) && channel.MemberUserIds.All(joinedUsers.Contains)
            };
        }
    }

    public ChatCryptoGroup ClaimCryptoGroup(Guid channelId, Guid deviceId, Guid userId)
    {
        lock (_sync)
        {
            RequireAccessibleChannelUnsafe(channelId, userId);
            RequireOwnedDeviceUnsafe(deviceId, userId);
            if (!IsUserEnabledUnsafe(userId))
            {
                throw new UnauthorizedAccessException("Chat is not enabled for this user.");
            }

            var existing = _state.CryptoGroups.FirstOrDefault(group => group.ChannelId == channelId);
            if (existing is not null)
            {
                throw new InvalidOperationException("The encrypted group was already created by another device.");
            }

            var package = _state.CryptoKeyPackages.FirstOrDefault(candidate =>
                candidate.ChannelId == channelId && candidate.DeviceId == deviceId && !candidate.IsConsumed)
                ?? throw new InvalidOperationException("Publish a key package before creating the encrypted group.");
            package.IsConsumed = true;
            var now = DateTime.UtcNow;
            var group = new ChatCryptoGroup
            {
                ChannelId = channelId,
                Epoch = 0,
                NextSequence = _state.CryptoEvents
                    .Where(item => item.ChannelId == channelId)
                    .Select(item => item.Sequence)
                    .DefaultIfEmpty(0)
                    .Max() + 1,
                MemberDeviceIds = new List<Guid> { deviceId },
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _state.CryptoGroups.Add(group);
            SaveUnsafe();
            return group.Copy();
        }
    }

    public ChatCryptoEvent CommitCryptoGroup(
        Guid channelId,
        Guid userId,
        string userName,
        CommitCryptoGroupRequest input)
    {
        lock (_sync)
        {
            if (input.EventId == Guid.Empty)
            {
                throw new InvalidOperationException("A unique commit identifier is required.");
            }

            RequireAccessibleChannelUnsafe(channelId, userId);
            RequireOwnedDeviceUnsafe(input.DeviceId, userId);
            if (!IsUserEnabledUnsafe(userId))
            {
                throw new UnauthorizedAccessException("Chat is not enabled for this user.");
            }

            var duplicate = _state.CryptoEvents.FirstOrDefault(item => item.Id == input.EventId);
            if (duplicate is not null)
            {
                if (duplicate.ChannelId != channelId
                    || duplicate.AuthorId != userId
                    || duplicate.SenderDeviceId != input.DeviceId
                    || duplicate.Kind != CryptoEventKinds.Commit)
                {
                    throw new UnauthorizedAccessException("The event identifier is already in use.");
                }

                return duplicate.Copy();
            }

            ValidateCryptoPayload(input.Payload, MaximumCryptoPayloadLength, MlsPrivateMessageWireFormat);
            var group = _state.CryptoGroups.FirstOrDefault(candidate => candidate.ChannelId == channelId)
                ?? throw new InvalidOperationException("The encrypted group has not been created.");
            if (group.Epoch != input.ExpectedEpoch || !group.MemberDeviceIds.Contains(input.DeviceId))
            {
                throw new InvalidOperationException("The encrypted group changed. Synchronize and try again.");
            }

            var target = TargetCryptoMembersUnsafe(channelId).ToHashSet();
            var requested = input.MemberDeviceIds.Where(id => id != Guid.Empty).ToHashSet();
            if (requested.Count == 0 || requested.Count > MaximumCryptoDevices || !requested.SetEquals(target))
            {
                throw new InvalidOperationException("The encrypted group membership is out of date.");
            }

            var previous = group.MemberDeviceIds.Distinct().ToList();
            var added = requested.Where(id => !previous.Contains(id)).OrderBy(id => id).ToList();
            var removed = previous.Where(id => !requested.Contains(id)).ToList();
            if (added.Count == 0 && removed.Count == 0)
            {
                throw new InvalidOperationException("An MLS commit must change the encrypted group membership.");
            }

            var welcomes = input.Welcomes.GroupBy(item => item.RecipientDeviceId).ToDictionary(grouping => grouping.Key, grouping => grouping.ToList());
            if (welcomes.Count != added.Count || added.Any(id => !welcomes.TryGetValue(id, out var items) || items.Count != 1))
            {
                throw new InvalidOperationException("Every added device requires exactly one MLS welcome.");
            }

            foreach (var addedDeviceId in added)
            {
                var package = _state.CryptoKeyPackages.FirstOrDefault(candidate =>
                    candidate.ChannelId == channelId && candidate.DeviceId == addedDeviceId && !candidate.IsConsumed)
                    ?? throw new InvalidOperationException("A key package is missing for an added device.");
                ValidateCryptoPayload(welcomes[addedDeviceId][0].Payload, MaximumCryptoPayloadLength, MlsWelcomeWireFormat);
                package.IsConsumed = true;
            }

            var newEpoch = checked(group.Epoch + 1);
            var commit = AddCryptoEventUnsafe(group, new ChatCryptoEvent
            {
                Id = input.EventId == Guid.Empty ? Guid.NewGuid() : input.EventId,
                ChannelId = channelId,
                Epoch = newEpoch,
                Kind = CryptoEventKinds.Commit,
                SenderDeviceId = input.DeviceId,
                AuthorId = userId,
                AuthorName = userName,
                Payload = input.Payload,
                AudienceDeviceIds = previous,
                CreatedAtUtc = DateTime.UtcNow
            });
            foreach (var addedDeviceId in added)
            {
                AddCryptoEventUnsafe(group, new ChatCryptoEvent
                {
                    Id = Guid.NewGuid(),
                    ChannelId = channelId,
                    Epoch = newEpoch,
                    Kind = CryptoEventKinds.Welcome,
                    SenderDeviceId = input.DeviceId,
                    RecipientDeviceId = addedDeviceId,
                    AuthorId = userId,
                    AuthorName = userName,
                    Payload = welcomes[addedDeviceId][0].Payload,
                    AudienceDeviceIds = new List<Guid> { addedDeviceId },
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            group.Epoch = newEpoch;
            group.MemberDeviceIds = requested.OrderBy(id => id).ToList();
            group.UpdatedAtUtc = DateTime.UtcNow;
            SaveUnsafe();
            return commit.Copy();
        }
    }

    public ChatCryptoEvent AddEncryptedMessage(
        Guid channelId,
        Guid userId,
        string userName,
        bool isAdministrator,
        CreateEncryptedMessageRequest input)
    {
        lock (_sync)
        {
            if (input.EventId == Guid.Empty)
            {
                throw new InvalidOperationException("A unique message identifier is required.");
            }

            var channel = RequireAccessibleChannelUnsafe(channelId, userId);
            RequireOwnedDeviceUnsafe(input.DeviceId, userId);
            if (!IsUserEnabledUnsafe(userId))
            {
                throw new UnauthorizedAccessException("Chat is not enabled for this user.");
            }

            var duplicate = _state.CryptoEvents.FirstOrDefault(item => item.Id == input.EventId);
            if (duplicate is not null)
            {
                if (duplicate.ChannelId != channelId
                    || duplicate.AuthorId != userId
                    || duplicate.SenderDeviceId != input.DeviceId
                    || duplicate.Kind != CryptoEventKinds.Application)
                {
                    throw new UnauthorizedAccessException("The event identifier is already in use.");
                }

                return duplicate.Copy();
            }

            if ((channel.Kind == ChatValues.AnnouncementKind || channel.Kind == ChatValues.RestrictedAnnouncementKind) && !isAdministrator)
            {
                throw new UnauthorizedAccessException("Only administrators can post in announcement channels.");
            }

            if (_state.MutedUsers.Any(mute => mute.UserId == userId) && !isAdministrator)
            {
                throw new UnauthorizedAccessException("You are muted from chat.");
            }

            ValidateCryptoPayload(input.Payload, 131072, MlsPrivateMessageWireFormat);
            var group = _state.CryptoGroups.FirstOrDefault(candidate => candidate.ChannelId == channelId)
                ?? throw new InvalidOperationException("The encrypted group has not been created.");
            if (group.Epoch != input.Epoch || !group.MemberDeviceIds.Contains(input.DeviceId))
            {
                throw new InvalidOperationException("The encrypted group changed. Synchronize and try again.");
            }

            if (channel.IsPrivate || channel.IsRestricted)
            {
                var joinedUsers = _state.CryptoDevices
                    .Where(device => group.MemberDeviceIds.Contains(device.Id) && !device.IsRevoked)
                    .Select(device => device.UserId).ToHashSet();
                if (!channel.MemberUserIds.All(joinedUsers.Contains))
                    throw new InvalidOperationException("Waiting for every participant to join the encrypted chat.");
            }

            var now = DateTime.UtcNow;
            var minimumInterval = TimeSpan.FromSeconds(Math.Clamp(_plugin.Configuration.MinimumSecondsBetweenMessages, 0, 300));
            var lastMessage = _state.CryptoEvents
                .Where(item => item.Kind == CryptoEventKinds.Application && item.AuthorId == userId)
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault();
            if (!isAdministrator && lastMessage is not null && now - lastMessage.CreatedAtUtc < minimumInterval)
            {
                throw new InvalidOperationException("Please wait before sending another message.");
            }

            var result = AddCryptoEventUnsafe(group, new ChatCryptoEvent
            {
                Id = input.EventId == Guid.Empty ? Guid.NewGuid() : input.EventId,
                ChannelId = channelId,
                Epoch = input.Epoch,
                Kind = CryptoEventKinds.Application,
                SenderDeviceId = input.DeviceId,
                AuthorId = userId,
                AuthorName = userName,
                Payload = input.Payload,
                AudienceDeviceIds = new List<Guid>(group.MemberDeviceIds),
                CreatedAtUtc = now
            });
            PruneExpiredCryptoEventsUnsafe();
            SaveUnsafe();
            return result.Copy();
        }
    }

    public ChatCryptoEvent? DeleteEncryptedMessage(Guid id, Guid userId, string userName, bool isAdministrator)
    {
        lock (_sync)
        {
            var target = _state.CryptoEvents.FirstOrDefault(item => item.Id == id && item.Kind == CryptoEventKinds.Application);
            if (target is null)
            {
                return null;
            }

            var group = _state.CryptoGroups.FirstOrDefault(candidate => candidate.ChannelId == target.ChannelId);
            if (group is null)
            {
                return null;
            }

            var targetChannel = _state.Channels.FirstOrDefault(channel => channel.Id == target.ChannelId);
            if (targetChannel is null || !CanAccessChannelUnsafe(targetChannel, userId))
                throw new UnauthorizedAccessException("You are not a member of this private chat.");
            if (!isAdministrator && target.AuthorId != userId)
                throw new UnauthorizedAccessException("You can only delete your own messages.");

            target.Payload = string.Empty;
            var deletion = AddCryptoEventUnsafe(group, new ChatCryptoEvent
            {
                Id = Guid.NewGuid(),
                ChannelId = target.ChannelId,
                Epoch = group.Epoch,
                Kind = CryptoEventKinds.Delete,
                SenderDeviceId = Guid.Empty,
                AuthorId = userId,
                AuthorName = userName,
                TargetEventId = target.Id,
                AudienceDeviceIds = new List<Guid>(group.MemberDeviceIds),
                CreatedAtUtc = DateTime.UtcNow
            });
            SaveUnsafe();
            return deletion.Copy();
        }
    }

    public ChatChannel AddChannel(CreateChannelRequest input)
    {
        lock (_sync)
        {
            EnsureUniqueChannelName(input.Name, null);
            var channel = new ChatChannel
            {
                Id = Guid.NewGuid(),
                Name = input.Name,
                Description = input.Description,
                Kind = input.IsRestricted && input.Kind == ChatValues.ChatKind ? ChatValues.RestrictedChatKind : input.IsRestricted && input.Kind == ChatValues.AnnouncementKind ? ChatValues.RestrictedAnnouncementKind : input.Kind,
                IsRestricted = input.IsRestricted,
                MemberUserIds = input.MemberUserIds.Distinct().ToList(),
                SortOrder = _state.Channels.Count == 0 ? 0 : _state.Channels.Max(item => item.SortOrder) + 1,
                CreatedAtUtc = DateTime.UtcNow
            };
            _state.Channels.Add(channel);
            SaveUnsafe();
            return channel.Copy();
        }
    }

    public ChatChannel? UpdateChannel(Guid id, UpdateChannelRequest input)
    {
        lock (_sync)
        {
            var channel = _state.Channels.FirstOrDefault(candidate => candidate.Id == id);
            if (channel is null)
            {
                return null;
            }

            if (channel.IsPrivate)
                throw new InvalidOperationException("Private conversations cannot be edited here.");

            EnsureUniqueChannelName(input.Name, id);
            if (channel.IsDefault)
            {
                input.Kind = ChatValues.ChatKind;
                input.IsArchived = false;
            }

            channel.Name = input.Name;
            channel.Description = input.Description;
            channel.Kind = input.Kind;
            channel.SortOrder = input.SortOrder;
            channel.IsArchived = input.IsArchived;
            channel.IsRestricted = input.IsRestricted;
            channel.MemberUserIds = input.IsRestricted && input.MemberUserIds.Count == 0
                ? new List<Guid>(channel.MemberUserIds)
                : input.MemberUserIds.Distinct().ToList();
            channel.Kind = input.IsRestricted && input.Kind == ChatValues.ChatKind ? ChatValues.RestrictedChatKind : input.IsRestricted && input.Kind == ChatValues.AnnouncementKind ? ChatValues.RestrictedAnnouncementKind : input.Kind;
            SaveUnsafe();
            return channel.Copy();
        }
    }

    public bool DeleteChannel(Guid id)
    {
        lock (_sync)
        {
            var channel = _state.Channels.FirstOrDefault(candidate => candidate.Id == id);
            if (channel is null || channel.IsDefault || channel.IsPrivate)
            {
                return false;
            }

            _state.Channels.Remove(channel);
            _state.Messages.RemoveAll(message => message.ChannelId == id);
            _state.CryptoKeyPackages.RemoveAll(package => package.ChannelId == id);
            _state.CryptoGroups.RemoveAll(group => group.ChannelId == id);
            _state.CryptoEvents.RemoveAll(item => item.ChannelId == id);
            SaveUnsafe();
            return true;
        }
    }

    public bool DeletePrivateChannel(Guid id, Guid userId)
    {
        lock (_sync)
        {
            var channel = _state.Channels.FirstOrDefault(candidate =>
                candidate.Id == id && candidate.IsPrivate && CanAccessChannelUnsafe(candidate, userId));
            if (channel is null)
            {
                return false;
            }

            _state.Channels.Remove(channel);
            _state.Messages.RemoveAll(message => message.ChannelId == id);
            _state.CryptoKeyPackages.RemoveAll(package => package.ChannelId == id);
            _state.CryptoGroups.RemoveAll(group => group.ChannelId == id);
            _state.CryptoEvents.RemoveAll(item => item.ChannelId == id);
            SaveUnsafe();
            return true;
        }
    }

    public bool DeleteMessage(Guid id, Guid userId)
    {
        lock (_sync)
        {
            var target = _state.Messages.FirstOrDefault(message => message.Id == id);
            if (target is null)
            {
                return false;
            }

            var channel = _state.Channels.FirstOrDefault(candidate => candidate.Id == target.ChannelId && !candidate.IsArchived);
            if (channel is null || !CanAccessChannelUnsafe(channel, userId))
            {
                throw new UnauthorizedAccessException("You are not a member of this private chat.");
            }

            var removed = _state.Messages.RemoveAll(message => message.Id == id) > 0;
            if (removed)
            {
                SaveUnsafe();
            }

            return removed;
        }
    }

    public IReadOnlyList<MutedUser> GetMutedUsers()
    {
        lock (_sync)
        {
            return _state.MutedUsers.OrderBy(item => item.UserName).Select(item => item.Copy()).ToList();
        }
    }

    public MutedUser Mute(MuteUserRequest input)
    {
        lock (_sync)
        {
            var mute = _state.MutedUsers.FirstOrDefault(item => item.UserId == input.UserId);
            if (mute is null)
            {
                mute = new MutedUser { UserId = input.UserId };
                _state.MutedUsers.Add(mute);
            }

            mute.UserName = input.UserName;
            mute.Reason = input.Reason;
            mute.MutedAtUtc = DateTime.UtcNow;
            SaveUnsafe();
            return mute.Copy();
        }
    }

    public bool Unmute(Guid userId)
    {
        lock (_sync)
        {
            var removed = _state.MutedUsers.RemoveAll(item => item.UserId == userId) > 0;
            if (removed)
            {
                SaveUnsafe();
            }

            return removed;
        }
    }

    public bool IsMuted(Guid? userId)
    {
        lock (_sync)
        {
            return userId.HasValue && _state.MutedUsers.Any(item => item.UserId == userId.Value);
        }
    }

    public bool IsChatEnabledFor(Guid? userId)
    {
        lock (_sync)
        {
            if (!_plugin.Configuration.ChatEnabled || !userId.HasValue)
            {
                return false;
            }

            return _state.UserAccessRules.FirstOrDefault(rule => rule.UserId == userId.Value)?.Enabled ?? true;
        }
    }

    public bool IsUserEnabled(Guid userId)
    {
        lock (_sync)
        {
            return _state.UserAccessRules.FirstOrDefault(rule => rule.UserId == userId)?.Enabled ?? true;
        }
    }

    public bool SetUserEnabled(Guid userId, bool enabled)
    {
        lock (_sync)
        {
            var rule = _state.UserAccessRules.FirstOrDefault(candidate => candidate.UserId == userId);
            if (rule is null)
            {
                _state.UserAccessRules.Add(new ChatUserAccessRule { UserId = userId, Enabled = enabled });
            }
            else
            {
                rule.Enabled = enabled;
            }

            SaveUnsafe();
            return enabled;
        }
    }

    public ChatSettings GetSettings()
    {
        lock (_sync)
        {
            return SettingsUnsafe();
        }
    }

    public ChatSettings SetSettings(ChatSettings settings)
    {
        lock (_sync)
        {
            _plugin.Configuration.ChatEnabled = settings.ChatEnabled;
            _plugin.Configuration.TabName = string.IsNullOrWhiteSpace(settings.TabName) ? "Chat" : settings.TabName.Trim();
            _plugin.Configuration.MessageLimit = Math.Clamp(settings.MessageLimit, 100, 4000);
            _plugin.Configuration.MinimumSecondsBetweenMessages = Math.Clamp(settings.MinimumSecondsBetweenMessages, 0, 300);
            _plugin.Configuration.RetentionDays = Math.Clamp(settings.RetentionDays, 1, 3650);
            _plugin.Configuration.ShowEncryptionDetails = settings.ShowEncryptionDetails;
            _plugin.SaveConfiguration();
            PruneExpiredMessagesUnsafe();
            SaveUnsafe();

            try
            {
                CustomTabsIntegration.EnsureTab(_plugin.Configuration.TabName);
            }
            catch
            {
                // Settings still save when the optional CustomTabs dependency is absent.
            }

            return SettingsUnsafe();
        }
    }

    private ChatState Load()
    {
        if (!File.Exists(_statePath))
        {
            return new ChatState();
        }

        try
        {
            return JsonSerializer.Deserialize<ChatState>(File.ReadAllText(_statePath), _jsonOptions) ?? new ChatState();
        }
        catch (JsonException)
        {
            var backupPath = _statePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(_statePath, backupPath, overwrite: false);
            return new ChatState();
        }
    }

    private void EnsureDefaults()
    {
        lock (_sync)
        {
            _state.Channels ??= new List<ChatChannel>();
            _state.Messages ??= new List<ChatMessage>();
            _state.MutedUsers ??= new List<MutedUser>();
            _state.UserAccessRules ??= new List<ChatUserAccessRule>();
            _state.CryptoDevices ??= new List<ChatCryptoDevice>();
            _state.CryptoKeyPackages ??= new List<ChatCryptoKeyPackage>();
            _state.CryptoGroups ??= new List<ChatCryptoGroup>();
            _state.CryptoEvents ??= new List<ChatCryptoEvent>();
            foreach (var channel in _state.Channels)
            {
                channel.MemberUserIds ??= new List<Guid>();
            }
            _state.SchemaVersion = 3;
            if (_state.Channels.Count != 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            _state.Channels.Add(new ChatChannel
            {
                Id = Guid.NewGuid(), Name = "Public", Description = "The shared chat for everyone on this Jellyfin server.",
                Kind = ChatValues.ChatKind, SortOrder = 0, IsDefault = true, CreatedAtUtc = now
            });
            _state.Channels.Add(new ChatChannel
            {
                Id = Guid.NewGuid(), Name = "Announcements", Description = "News and notices from the administrators.",
                Kind = ChatValues.AnnouncementKind, SortOrder = 1, CreatedAtUtc = now
            });
        }
    }

    private void EnsureUniqueChannelName(string name, Guid? exceptId)
    {
        if (_state.Channels.Any(channel => channel.Id != exceptId && string.Equals(channel.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A channel with this name already exists.");
        }
    }

    private bool PruneExpiredMessagesUnsafe()
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(_plugin.Configuration.RetentionDays, 1, 3650));
        return _state.Messages.RemoveAll(message => message.CreatedAtUtc < cutoff) > 0;
    }

    private bool PruneExpiredCryptoEventsUnsafe()
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(_plugin.Configuration.RetentionDays, 1, 3650));
        return _state.CryptoEvents.RemoveAll(item =>
            item.CreatedAtUtc < cutoff
            && (item.Kind == CryptoEventKinds.Application || item.Kind == CryptoEventKinds.Delete)) > 0;
    }

    private bool ResetOrphanedCryptoGroupUnsafe(Guid channelId)
    {
        var group = _state.CryptoGroups.FirstOrDefault(candidate => candidate.ChannelId == channelId);
        if (group is null)
        {
            return false;
        }

        var activeCutoff = DateTime.UtcNow.AddMinutes(-10);
        var canAdvanceGroup = _state.CryptoDevices.Any(device =>
            group.MemberDeviceIds.Contains(device.Id)
            && !device.IsRevoked
            && IsUserEnabledUnsafe(device.UserId)
            && device.LastSeenAtUtc >= activeCutoff);
        if (canAdvanceGroup)
        {
            return false;
        }

        // A browser origin change (for example moving Jellyfin from :8920 to :443)
        // creates new device identities. If every old group member is offline while
        // a current device is waiting with a fresh key package, let that device
        // recover the channel instead of waiting forever for an unreachable key.
        var hasActiveReplacement = _state.CryptoKeyPackages.Any(package =>
            package.ChannelId == channelId
            && !package.IsConsumed
            && _state.CryptoDevices.Any(device =>
                device.Id == package.DeviceId
                && !device.IsRevoked
                && IsUserEnabledUnsafe(device.UserId)
                && device.LastSeenAtUtc >= activeCutoff));
        if (!hasActiveReplacement)
        {
            return false;
        }

        _state.CryptoGroups.Remove(group);
        _state.CryptoEvents.RemoveAll(item => item.ChannelId == channelId
            && (item.Kind == CryptoEventKinds.Commit || item.Kind == CryptoEventKinds.Welcome));
        _state.CryptoKeyPackages.RemoveAll(package => package.ChannelId == channelId && package.IsConsumed);
        return true;
    }

    private ChatChannel RequireActiveChannelUnsafe(Guid channelId)
    {
        return _state.Channels.FirstOrDefault(channel => channel.Id == channelId && !channel.IsArchived)
            ?? throw new InvalidOperationException("The channel does not exist or is archived.");
    }

    private ChatChannel RequireAccessibleChannelUnsafe(Guid channelId, Guid userId)
    {
        var channel = RequireActiveChannelUnsafe(channelId);
        if (!CanAccessChannelUnsafe(channel, userId))
            throw new UnauthorizedAccessException("You are not a member of this private chat.");
        return channel;
    }

    private static bool CanAccessChannelUnsafe(ChatChannel channel, Guid userId)
    {
        return !channel.IsPrivate && !channel.IsRestricted
            || (channel.MemberUserIds ?? new List<Guid>()).Contains(userId);
    }

    private ChatCryptoDevice RequireOwnedDeviceUnsafe(Guid deviceId, Guid userId)
    {
        var device = _state.CryptoDevices.FirstOrDefault(candidate => candidate.Id == deviceId && !candidate.IsRevoked)
            ?? throw new InvalidOperationException("The encrypted-chat device is not registered.");
        if (device.UserId != userId)
        {
            throw new UnauthorizedAccessException("This encrypted-chat device belongs to another user.");
        }

        return device;
    }

    private bool IsUserEnabledUnsafe(Guid userId)
    {
        return _plugin.Configuration.ChatEnabled
            && (_state.UserAccessRules.FirstOrDefault(rule => rule.UserId == userId)?.Enabled ?? true);
    }

    private IEnumerable<Guid> TargetCryptoMembersUnsafe(Guid channelId)
    {
        var channel = _state.Channels.FirstOrDefault(item => item.Id == channelId);
        var group = _state.CryptoGroups.FirstOrDefault(candidate => candidate.ChannelId == channelId);
        var eligible = _state.CryptoDevices
            .Where(device => !device.IsRevoked && IsUserEnabledUnsafe(device.UserId)
                && (channel is null || (!channel.IsPrivate && !channel.IsRestricted
                    || (channel.MemberUserIds ?? new List<Guid>()).Contains(device.UserId))))
            .Select(device => device.Id)
            .ToHashSet();
        var members = group?.MemberDeviceIds.Where(eligible.Contains) ?? Enumerable.Empty<Guid>();
        var joinable = _state.CryptoKeyPackages
            .Where(package => package.ChannelId == channelId && !package.IsConsumed && eligible.Contains(package.DeviceId))
            .Select(package => package.DeviceId);
        return members.Concat(joinable).Distinct();
    }

    private ChatCryptoEvent AddCryptoEventUnsafe(ChatCryptoGroup group, ChatCryptoEvent item)
    {
        item.Sequence = group.NextSequence;
        group.NextSequence = checked(group.NextSequence + 1);
        _state.CryptoEvents.Add(item);
        return item;
    }

    private static void ValidateCryptoPayload(string payload, int maximumLength, byte expectedWireFormat)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > maximumLength)
        {
            throw new InvalidOperationException("The encrypted payload has an invalid size.");
        }

        try
        {
            var decoded = Convert.FromBase64String(payload);
            if (decoded.Length < 8
                || decoded[0] != 0
                || decoded[1] != 1
                || decoded[2] != 0
                || decoded[3] != expectedWireFormat)
            {
                throw new InvalidOperationException("The encrypted payload is not the expected MLS 1.0 message type.");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The encrypted payload must be valid Base64.", exception);
        }
    }

    private ChatSettings SettingsUnsafe() => new()
    {
        ChatEnabled = _plugin.Configuration.ChatEnabled,
        TabName = string.IsNullOrWhiteSpace(_plugin.Configuration.TabName) ? "Chat" : _plugin.Configuration.TabName.Trim(),
        MessageLimit = Math.Clamp(_plugin.Configuration.MessageLimit, 100, 4000),
        MinimumSecondsBetweenMessages = Math.Clamp(_plugin.Configuration.MinimumSecondsBetweenMessages, 0, 300),
        RetentionDays = Math.Clamp(_plugin.Configuration.RetentionDays, 1, 3650),
        ShowEncryptionDetails = _plugin.Configuration.ShowEncryptionDetails
    };

    private void Save()
    {
        lock (_sync)
        {
            SaveUnsafe();
        }
    }

    private void SaveUnsafe()
    {
        var temporaryPath = _statePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_state, _jsonOptions));
        File.Move(temporaryPath, _statePath, overwrite: true);
    }
}
