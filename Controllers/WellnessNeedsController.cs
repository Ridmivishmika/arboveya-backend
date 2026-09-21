using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Route("api/wellness-needs")]
public class WellnessNeedsController : ControllerBase
{
    private readonly IWellnessNeedService _wellnessNeedService;
    private readonly ILogger<WellnessNeedsController> _logger;

    public WellnessNeedsController(IWellnessNeedService wellnessNeedService, ILogger<WellnessNeedsController> logger)
    {
        _wellnessNeedService = wellnessNeedService;
        _logger = logger;
    }

    /// <summary>
    /// Get all wellness needs with optional search filtering (Public)
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<WellnessNeedResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll([FromQuery] string? search, [FromQuery] bool onlyWithProducts = false)
    {
        var wellnessNeeds = await _wellnessNeedService.GetAllAsync(search, onlyWithProducts);
        return Ok(wellnessNeeds);
    }

    /// <summary>
    /// Get a wellness need by its ID (Public)
    /// </summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(WellnessNeedResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var wellnessNeed = await _wellnessNeedService.GetByIdAsync(id);
        if (wellnessNeed == null)
        {
            return NotFound(new { message = $"Wellness need with ID '{id}' was not found." });
        }

        return Ok(wellnessNeed);
    }

    /// <summary>
    /// Get a wellness need by its unique Name (Public)
    /// </summary>
    [HttpGet("by-name/{name}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(WellnessNeedResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Wellness need name is required." });
        }

        var wellnessNeed = await _wellnessNeedService.GetByNameAsync(name);
        if (wellnessNeed == null)
        {
            return NotFound(new { message = $"Wellness need with name '{name}' was not found." });
        }

        return Ok(wellnessNeed);
    }

    /// <summary>
    /// Create a new wellness need (Admin only / demo allowed)
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(WellnessNeedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateWellnessNeedDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var created = await _wellnessNeedService.CreateAsync(request);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating wellness need '{Name}'", request.Name);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while creating the wellness need." });
        }
    }

    /// <summary>
    /// Update an existing wellness need (Admin only / demo allowed)
    /// </summary>
    [HttpPut("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(WellnessNeedResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWellnessNeedDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _wellnessNeedService.UpdateAsync(id, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Wellness need with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating wellness need '{Id}'", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating the wellness need." });
        }
    }

    /// <summary>
    /// Delete a wellness need (Admin only / demo allowed)
    /// </summary>
    [HttpDelete("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            var deleted = await _wellnessNeedService.DeleteAsync(id);
            if (!deleted)
            {
                return NotFound(new { message = $"Wellness need with ID '{id}' was not found." });
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting wellness need '{Id}'", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while deleting the wellness need." });
        }
    }
}
