using System.Security.Claims;
using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/admin/conversations")]
[Authorize(Roles = "Admin")]
public class AdminConversationsController : ControllerBase
{
    private readonly IConversationService _conversationService;
    private readonly ILogger<AdminConversationsController> _logger;

    public AdminConversationsController(IConversationService conversationService, ILogger<AdminConversationsController> logger)
    {
        _conversationService = conversationService;
        _logger = logger;
    }

    private bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrEmpty(claim) && Guid.TryParse(claim, out userId);
    }

    /// <summary>
    /// View all buyer-seller conversations across the store (Admin only)
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ConversationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllConversations(
        [FromQuery] string? type,
        [FromQuery] bool? unreadOnly,
        [FromQuery] string? search)
    {
        try
        {
            var conversations = await _conversationService.GetAllConversationsForAdminAsync(type, unreadOnly, search);
            return Ok(conversations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving conversations for admin.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while loading conversations." });
        }
    }

    /// <summary>
    /// View a specific conversation by ID (Admin only)
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetConversationById(Guid id)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid admin identity." });
        }

        try
        {
            var conversation = await _conversationService.GetConversationByIdAsync(id, userId, "Admin");
            if (conversation == null)
            {
                return NotFound(new { message = $"Conversation '{id}' was not found." });
            }

            return Ok(conversation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving conversation {ConversationId} for admin", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while loading the conversation." });
        }
    }

    /// <summary>
    /// Get the unread buyer conversations count for Admin
    /// </summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAdminUnreadCount()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid admin identity." });
        }

        var count = await _conversationService.GetUnreadCountAsync(userId, "Admin");
        return Ok(new UnreadCountDto { UnreadCount = count });
    }

    /// <summary>
    /// Send an admin reply or moderation message to the buyer
    /// </summary>
    [HttpPost("{id:guid}/messages")]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendAdminMessage(Guid id, [FromBody] CreateMessageDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!TryGetUserId(out var adminId))
        {
            return Unauthorized(new { message = "Invalid admin identity." });
        }

        try
        {
            var message = await _conversationService.SendMessageAsync(id, adminId, "Admin", request.MessageText);
            return Ok(message);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending admin message to conversation {ConversationId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while sending the message." });
        }
    }
}
