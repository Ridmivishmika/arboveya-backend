using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SiteSettingsController : ControllerBase
{
    private readonly ISiteSettingsService _settingsService;
    private readonly ILogger<SiteSettingsController> _logger;

    public SiteSettingsController(ISiteSettingsService settingsService, ILogger<SiteSettingsController> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Get dynamic site settings and content (Public)
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(SiteSettingsResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await _settingsService.GetSettingsAsync();
        return Ok(settings);
    }

    /// <summary>
    /// Update dynamic site settings and content (Admin only)
    /// </summary>
    [HttpPut]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(SiteSettingsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSiteSettingsDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _settingsService.UpdateSettingsAsync(request);
            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating site settings.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating site settings." });
        }
    }
}
