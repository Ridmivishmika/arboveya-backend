using System.Security.Claims;
using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ContactController : ControllerBase
{
    private readonly IContactMessageService _contactService;
    private readonly ILogger<ContactController> _logger;

    public ContactController(IContactMessageService contactService, ILogger<ContactController> logger)
    {
        _contactService = contactService;
        _logger = logger;
    }

    /// <summary>
    /// Submit a contact form inquiry (Public or Authenticated)
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ContactMessageResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitMessage([FromBody] CreateContactMessageDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var currentUserId = GetCurrentUserId();

        try
        {
            var created = await _contactService.SubmitMessageAsync(currentUserId, request);
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error submitting contact message.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while sending your message." });
        }
    }

    /// <summary>
    /// Get all contact inquiries with status and keyword filtering (Admin only)
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(IEnumerable<ContactMessageResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllMessages([FromQuery] string? status, [FromQuery] string? search)
    {
        var messages = await _contactService.GetAllMessagesAsync(status, search);
        return Ok(messages);
    }

    /// <summary>
    /// Get an inquiry by ID (Admin only, automatically marks as Read)
    /// </summary>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ContactMessageResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var message = await _contactService.GetByIdAsync(id, markAsRead: true);
        if (message == null)
        {
            return NotFound(new { message = $"Contact inquiry with ID '{id}' was not found." });
        }

        return Ok(message);
    }

    /// <summary>
    /// Update inquiry status and admin notes (Admin only)
    /// </summary>
    [HttpPut("{id:guid}/status")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ContactMessageResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateContactMessageStatusDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _contactService.UpdateStatusAsync(id, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Contact inquiry with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating contact inquiry status {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating the inquiry status." });
        }
    }

    /// <summary>
    /// Delete a contact inquiry (Admin only)
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            var deleted = await _contactService.DeleteMessageAsync(id);
            if (!deleted)
            {
                return NotFound(new { message = $"Contact inquiry with ID '{id}' was not found." });
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting contact inquiry {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while deleting the inquiry." });
        }
    }

    private Guid? GetCurrentUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var userId) ? userId : null;
    }
}
