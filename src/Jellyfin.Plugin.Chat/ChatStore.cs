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

    public ChatMessage AddMessage(Guid channelId, Guid? authorId, string authorName, string body, bool isAdministrator)
    {
        lock (_sync)
        {
            var channel = _state.Channels.FirstOrDefault(candidate => candidate.Id == channelId && !candidate.IsArchived)
                ?? throw new InvalidOperationException("The channel does not exist or is archived.");
            if (channel.Kind == ChatValues.AnnouncementKind && !isAdministrator)
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
                Kind = input.Kind,
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
            SaveUnsafe();
            return channel.Copy();
        }
    }

    public bool DeleteChannel(Guid id)
    {
        lock (_sync)
        {
            var channel = _state.Channels.FirstOrDefault(candidate => candidate.Id == id);
            if (channel is null || channel.IsDefault)
            {
                return false;
            }

            _state.Channels.Remove(channel);
            _state.Messages.RemoveAll(message => message.ChannelId == id);
            SaveUnsafe();
            return true;
        }
    }

    public bool DeleteMessage(Guid id)
    {
        lock (_sync)
        {
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

    private ChatSettings SettingsUnsafe() => new()
    {
        ChatEnabled = _plugin.Configuration.ChatEnabled,
        TabName = string.IsNullOrWhiteSpace(_plugin.Configuration.TabName) ? "Chat" : _plugin.Configuration.TabName.Trim(),
        MessageLimit = Math.Clamp(_plugin.Configuration.MessageLimit, 100, 4000),
        MinimumSecondsBetweenMessages = Math.Clamp(_plugin.Configuration.MinimumSecondsBetweenMessages, 0, 300),
        RetentionDays = Math.Clamp(_plugin.Configuration.RetentionDays, 1, 3650)
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
