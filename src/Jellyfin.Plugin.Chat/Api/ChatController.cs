using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Chat.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Chat.Api;

[ApiController]
[Route("JellyfinChat")]
public sealed class ChatController : ControllerBase
{
    private readonly IUserManager _userManager;

    public ChatController(IUserManager userManager)
    {
        _userManager = userManager;
    }

    [HttpGet("App")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public ActionResult GetApp()
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Chat.Web.chat-app.html");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        Response.Headers.CacheControl = "no-store";
        return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
    }

    [HttpGet("Bootstrap")]
    [Authorize]
    public ActionResult<ChatBootstrap> Bootstrap()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Problem("Jellyfin Chat is not ready.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var user = CurrentUser();
        var isEnabled = plugin.Store.IsChatEnabledFor(user.Id);
        return Ok(new ChatBootstrap
        {
            Channels = isEnabled ? plugin.Store.GetChannels(includeArchived: false) : Array.Empty<ChatChannel>(),
            IsAdministrator = user.IsAdministrator,
            IsMuted = plugin.Store.IsMuted(user.Id),
            IsEnabled = isEnabled,
            CurrentUserName = user.Name,
            MessageLimit = plugin.Store.GetSettings().MessageLimit
        });
    }

    [HttpGet("Messages")]
    [Authorize]
    public ActionResult GetMessages([FromQuery] Guid channelId, [FromQuery] DateTime? afterUtc = null, [FromQuery] int limit = 100)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Problem("Jellyfin Chat is not ready.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return plugin.Store.IsChatEnabledFor(CurrentUser().Id)
            ? Ok(plugin.Store.GetMessages(channelId, afterUtc, limit))
            : StatusCode(StatusCodes.Status403Forbidden, new { Message = "Chat is not enabled for this user." });
    }

    [HttpPost("Messages")]
    [Authorize]
    public ActionResult<ChatMessage> CreateMessage([FromBody] CreateMessageRequest input)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Problem("Jellyfin Chat is not ready.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var user = CurrentUser();
        if (!plugin.Store.IsChatEnabledFor(user.Id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = "Chat is not enabled for this user." });
        }

        var body = input.Body?.Trim() ?? string.Empty;
        var limit = plugin.Store.GetSettings().MessageLimit;
        if (body.Length == 0 || body.Length > limit)
        {
            return BadRequest(new { Message = $"Messages must contain between 1 and {limit} characters." });
        }

        try
        {
            var message = plugin.Store.AddMessage(input.ChannelId, user.Id, user.Name, body, user.IsAdministrator);
            return StatusCode(StatusCodes.Status201Created, message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }

    [HttpDelete("Messages/{id:guid}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult DeleteMessage([FromRoute] Guid id)
    {
        return Plugin.Instance?.Store.DeleteMessage(id) == true ? NoContent() : NotFound();
    }

    [HttpGet("Channels")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult GetAllChannels()
    {
        return Plugin.Instance is { } plugin ? Ok(plugin.Store.GetChannels(includeArchived: true)) : NotFound();
    }

    [HttpPost("Channels")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<ChatChannel> CreateChannel([FromBody] CreateChannelRequest input)
    {
        var validation = NormalizeChannel(input.Name, input.Description, input.Kind);
        if (validation.Error is not null)
        {
            return BadRequest(new { Message = validation.Error });
        }

        input.Name = validation.Name!;
        input.Description = validation.Description!;
        input.Kind = validation.Kind!;
        try
        {
            var channel = Plugin.Instance?.Store.AddChannel(input);
            return channel is null ? NotFound() : StatusCode(StatusCodes.Status201Created, channel);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }

    [HttpPut("Channels/{id:guid}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<ChatChannel> UpdateChannel([FromRoute] Guid id, [FromBody] UpdateChannelRequest input)
    {
        var validation = NormalizeChannel(input.Name, input.Description, input.Kind);
        if (validation.Error is not null)
        {
            return BadRequest(new { Message = validation.Error });
        }

        input.Name = validation.Name!;
        input.Description = validation.Description!;
        input.Kind = validation.Kind!;
        try
        {
            var channel = Plugin.Instance?.Store.UpdateChannel(id, input);
            return channel is null ? NotFound() : Ok(channel);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new { Message = exception.Message });
        }
    }

    [HttpDelete("Channels/{id:guid}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult DeleteChannel([FromRoute] Guid id)
    {
        return Plugin.Instance?.Store.DeleteChannel(id) == true ? NoContent() : BadRequest(new { Message = "Default channels cannot be deleted." });
    }

    [HttpGet("Mutes")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult GetMutes() => Plugin.Instance is { } plugin ? Ok(plugin.Store.GetMutedUsers()) : NotFound();

    [HttpPost("Mutes")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<MutedUser> Mute([FromBody] MuteUserRequest input)
    {
        if (input.UserId == Guid.Empty || string.IsNullOrWhiteSpace(input.UserName))
        {
            return BadRequest(new { Message = "A Jellyfin user is required." });
        }

        input.UserName = input.UserName.Trim();
        input.Reason = input.Reason?.Trim() ?? string.Empty;
        return Plugin.Instance is { } plugin ? Ok(plugin.Store.Mute(input)) : NotFound();
    }

    [HttpDelete("Mutes/{userId:guid}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Unmute([FromRoute] Guid userId)
    {
        return Plugin.Instance?.Store.Unmute(userId) == true ? NoContent() : NotFound();
    }

    [HttpGet("Settings")]
    [Authorize]
    public ActionResult<ChatSettings> GetSettings()
    {
        return Plugin.Instance is { } plugin ? Ok(plugin.Store.GetSettings()) : NotFound();
    }

    [HttpPut("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<ChatSettings> SetSettings([FromBody] ChatSettings input)
    {
        if (string.IsNullOrWhiteSpace(input.TabName) || input.TabName.Trim().Length > 40)
        {
            return BadRequest(new { Message = "The tab name must contain between 1 and 40 characters." });
        }

        return Plugin.Instance is { } plugin ? Ok(plugin.Store.SetSettings(input)) : NotFound();
    }

    [HttpGet("Availability")]
    [Authorize]
    public ActionResult GetAvailability()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return Problem("Jellyfin Chat is not ready.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var user = CurrentUser();
        var settings = plugin.Store.GetSettings();
        return Ok(new
        {
            Available = plugin.Store.IsChatEnabledFor(user.Id),
            settings.ChatEnabled,
            settings.TabName
        });
    }

    [HttpGet("Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult GetUsers()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return NotFound();
        }

        var users = _userManager.GetUsers()
            .Where(user => user.LastLoginDate.HasValue)
            .OrderBy(user => user.Username)
            .Select(user => new ChatUserAccess
            {
                UserId = user.Id,
                UserName = user.Username,
                Enabled = plugin.Store.IsUserEnabled(user.Id),
                IsAdministrator = user.HasPermission(PermissionKind.IsAdministrator),
                LastLoginDateUtc = user.LastLoginDate
            })
            .ToList();
        return Ok(users);
    }

    [HttpPut("Users/{userId:guid}/Access")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SetUserAccess([FromRoute] Guid userId, [FromBody] SetUserAccessRequest input)
    {
        if (_userManager.GetUserById(userId) is null)
        {
            return NotFound();
        }

        var enabled = Plugin.Instance?.Store.SetUserEnabled(userId, input.Enabled);
        return enabled.HasValue ? Ok(new { Enabled = enabled.Value }) : NotFound();
    }

    private (Guid? Id, string Name, bool IsAdministrator) CurrentUser()
    {
        var name = User.Identity?.Name ?? string.Empty;
        var user = _userManager.GetUserByName(name);
        return (user?.Id, string.IsNullOrWhiteSpace(name) ? "Jellyfin administrator" : name, User.IsInRole("Administrator"));
    }

    private static (string? Name, string? Description, string? Kind, string? Error) NormalizeChannel(string? name, string? description, string? kind)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        var normalizedDescription = description?.Trim() ?? string.Empty;
        var normalizedKind = ChatValues.CanonicalKind(kind);
        if (normalizedName.Length == 0 || normalizedName.Length > 60)
        {
            return (null, null, null, "Channel names must contain between 1 and 60 characters.");
        }

        if (normalizedDescription.Length > 300)
        {
            return (null, null, null, "Channel descriptions cannot exceed 300 characters.");
        }

        return normalizedKind is null
            ? (null, null, null, "Choose Chat or Announcement as the channel type.")
            : (normalizedName, normalizedDescription, normalizedKind, null);
    }
}
