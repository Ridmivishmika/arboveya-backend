using System.Security.Claims;
using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Route("api/blogs")]
public class BlogPostsController : ControllerBase
{
    private readonly IBlogPostService _blogService;
    private readonly ILogger<BlogPostsController> _logger;

    public BlogPostsController(IBlogPostService blogService, ILogger<BlogPostsController> logger)
    {
        _blogService = blogService;
        _logger = logger;
    }

    /// <summary>
    /// Get all published (approved) blog posts (Public)
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<BlogPostResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllPublished([FromQuery] string? search)
    {
        var blogs = await _blogService.GetPublishedBlogsAsync(search);
        return Ok(blogs);
    }

    /// <summary>
    /// Get a blog post by its ID (Public if approved; accessible by author or admin if pending)
    /// </summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(BlogPostResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var currentUserId = GetCurrentUserId();
        var isAdmin = User.IsInRole("Admin");

        var blog = await _blogService.GetByIdAsync(id, currentUserId, isAdmin);
        if (blog == null)
        {
            return NotFound(new { message = $"Blog post with ID '{id}' was not found or is awaiting approval." });
        }

        return Ok(blog);
    }

    /// <summary>
    /// Get all blog posts submitted by the currently authenticated user (including pending)
    /// </summary>
    [HttpGet("my-blogs")]
    [Authorize]
    [ProducesResponseType(typeof(IEnumerable<BlogPostResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyBlogs()
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var blogs = await _blogService.GetUserBlogsAsync(userId.Value);
        return Ok(blogs);
    }

    /// <summary>
    /// Create a new blog post (Authenticated Users & Admin)
    /// </summary>
    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(BlogPostResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateBlogPostDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var isAdmin = User.IsInRole("Admin");

        try
        {
            var created = await _blogService.CreateBlogAsync(userId.Value, isAdmin, request);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating blog post '{Title}'", request.Title);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while creating the blog post." });
        }
    }

    /// <summary>
    /// Update a blog post (Author or Admin)
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(BlogPostResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBlogPostDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var isAdmin = User.IsInRole("Admin");

        try
        {
            var updated = await _blogService.UpdateBlogAsync(id, userId.Value, isAdmin, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Blog post with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating blog post {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating the blog post." });
        }
    }

    /// <summary>
    /// Delete a blog post (Author or Admin can delete)
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized(new { message = "Invalid user identity." });
        }

        var isAdmin = User.IsInRole("Admin");

        try
        {
            var deleted = await _blogService.DeleteBlogAsync(id, userId.Value, isAdmin);
            if (!deleted)
            {
                return NotFound(new { message = $"Blog post with ID '{id}' was not found." });
            }

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting blog post {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while deleting the blog post." });
        }
    }

    /// <summary>
    /// Moderation queue: List all blogs with approval status filtering (Admin only)
    /// </summary>
    [HttpGet("moderation")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(IEnumerable<BlogPostResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetModerationList([FromQuery] bool? isApproved, [FromQuery] string? search)
    {
        var blogs = await _blogService.GetModerationListAsync(isApproved, search);
        return Ok(blogs);
    }

    /// <summary>
    /// Moderate a blog post: Approve or reject (Admin only)
    /// </summary>
    [HttpPut("{id:guid}/moderation")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(BlogPostResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Moderate(Guid id, [FromBody] BlogModerationDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var moderated = await _blogService.ModerateBlogAsync(id, request.IsApproved);
            if (moderated == null)
            {
                return NotFound(new { message = $"Blog post with ID '{id}' was not found." });
            }

            return Ok(moderated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error moderating blog post {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while moderating the blog post." });
        }
    }

    private Guid? GetCurrentUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var userId) ? userId : null;
    }
}
