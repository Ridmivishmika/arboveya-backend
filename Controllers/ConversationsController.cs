using System.Security.Claims;
using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly IConversationService _conversationService;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(IConversationService conversationService, ILogger<ConversationsController> logger)
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

    private string GetUserRole()
    {
        return User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirst("role")?.Value ?? "Customer";
    }

    /// <summary>
    /// Start or retrieve a product inquiry conversation for the authenticated buyer
    /// </summary>
    [HttpPost("product/{productId:guid}")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> StartProductConversation(Guid productId, [FromBody] CreateConversationDto? request)
    {
        if (!TryGetUserId(out var buyerId))
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        try
        {
            var conversation = await _conversationService.GetOrCreateProductConversationAsync(
                buyerId, productId, request?.InitialMessage);

            return Ok(conversation);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting product conversation for buyer {BuyerId} and product {ProductId}", buyerId, productId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while creating or loading the conversation." });
        }
    }

    /// <summary>
    /// Start or retrieve an order support conversation for the authenticated buyer
    /// </summary>
    [HttpPost("order/{orderId:guid}")]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> StartOrderConversation(Guid orderId, [FromBody] CreateConversationDto? request)
    {
        if (!TryGetUserId(out var buyerId))
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        try
        {
            var conversation = await _conversationService.GetOrCreateOrderConversationAsync(
                buyerId, orderId, request?.InitialMessage);

            return Ok(conversation);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting order conversation for buyer {BuyerId} and order {OrderId}", buyerId, orderId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while creating or loading the conversation." });
        }
    }

    /// <summary>
    /// Get all conversations for the authenticated buyer (or seller if role is Seller)
    /// </summary>
    [HttpGet("my")]
    [ProducesResponseType(typeof(IEnumerable<ConversationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyConversations()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var role = GetUserRole();
        try
        {
            if (string.Equals(role, "Seller", StringComparison.OrdinalIgnoreCase))
            {
                var sellerConversations = await _conversationService.GetSellerConversationsAsync(userId);
                return Ok(sellerConversations);
            }

            var buyerConversations = await _conversationService.GetBuyerConversationsAsync(userId);
            return Ok(buyerConversations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving conversations for user {UserId}", userId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while loading conversations." });
        }
    }

    /// <summary>
    /// Get a single conversation by ID with access control validation
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
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var role = GetUserRole();
        try
        {
            var conversation = await _conversationService.GetConversationByIdAsync(id, userId, role);
            if (conversation == null)
            {
                return NotFound(new { message = $"Conversation '{id}' was not found." });
            }

            return Ok(conversation);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving conversation {ConversationId} for user {UserId}", id, userId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while loading the conversation." });
        }
    }

    /// <summary>
    /// Get messages for a conversation
    /// </summary>
    [HttpGet("{id:guid}/messages")]
    [ProducesResponseType(typeof(IEnumerable<MessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMessages(Guid id)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var role = GetUserRole();
        try
        {
            var messages = await _conversationService.GetMessagesAsync(id, userId, role);
            return Ok(messages);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving messages for conversation {ConversationId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while loading messages." });
        }
    }

    /// <summary>
    /// Send a message within an existing conversation
    /// </summary>
    [HttpPost("{id:guid}/messages")]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendMessage(Guid id, [FromBody] CreateMessageDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!TryGetUserId(out var senderId))
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var role = GetUserRole();
        try
        {
            var message = await _conversationService.SendMessageAsync(id, senderId, role, request.MessageText);
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
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message in conversation {ConversationId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while sending your message." });
        }
    }

    /// <summary>
    /// Mark a conversation as read by the authenticated user
    /// </summary>
    [HttpPut("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkAsRead(Guid id)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var role = GetUserRole();
        var success = await _conversationService.MarkConversationAsReadAsync(id, userId, role);
        if (!success)
        {
            return NotFound(new { message = "Conversation not found or access denied." });
        }

        return Ok(new { success = true });
    }

    /// <summary>
    /// Get the total unread conversations count for the authenticated user
    /// </summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadCountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUnreadCount()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var role = GetUserRole();
        var count = await _conversationService.GetUnreadCountAsync(userId, role);
        return Ok(new UnreadCountDto { UnreadCount = count });
    }
}
