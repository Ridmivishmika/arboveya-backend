using System.Security.Claims;
using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Route("api/reviews")]
public class ProductReviewsController : ControllerBase
{
    private readonly IProductReviewService _reviewService;
    private readonly ILogger<ProductReviewsController> _logger;

    public ProductReviewsController(IProductReviewService reviewService, ILogger<ProductReviewsController> logger)
    {
        _reviewService = reviewService;
        _logger = logger;
    }

    /// <summary>
    /// Get all customer reviews for a specific product (Public - immediate visibility)
    /// </summary>
    [HttpGet("/api/products/{productId}/reviews")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<ProductReviewResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProductReviews(string productId)
    {
        Guid pGuid = Guid.TryParse(productId, out var parsed) ? parsed : Guid.Empty;
        var reviews = await _reviewService.GetProductReviewsAsync(pGuid, approvedOnly: false);
        return Ok(reviews);
    }

    /// <summary>
    /// Submit a public review for a product from the customer storefront (Public)
    /// </summary>
    [HttpPost("/api/products/{productId}/public-reviews")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductReviewResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SubmitPublicReview(string productId, [FromBody] PublicProductReviewDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        Guid pGuid = Guid.TryParse(productId, out var parsed) ? parsed : Guid.Empty;
        var currentUserId = GetCurrentUserId();

        try
        {
            var created = await _reviewService.CreatePublicReviewAsync(pGuid, request, currentUserId);
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (ArgumentException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error submitting public review for product {ProductId}", productId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while submitting your review." });
        }
    }

    /// <summary>
    /// Get all customer reviews or filter by user / product
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<ProductReviewResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllReviews([FromQuery] Guid? productId, [FromQuery] Guid? userId, [FromQuery] bool? isApproved)
    {
        var targetUserId = userId ?? GetCurrentUserId();
        var reviews = await _reviewService.GetAllReviewsAsync(isApproved, productId, targetUserId);
        return Ok(reviews);
    }

    /// <summary>
    /// Get all reviews submitted by the current user
    /// </summary>
    [HttpGet("my-reviews")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<ProductReviewResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyReviews([FromQuery] Guid? userId, [FromQuery] string? email)
    {
        var targetUserId = userId ?? GetCurrentUserId();
        if (!targetUserId.HasValue && string.IsNullOrWhiteSpace(email))
        {
            return Ok(Array.Empty<ProductReviewResponseDto>());
        }

        var reviews = await _reviewService.GetAllReviewsAsync(null, null, targetUserId, email);
        return Ok(reviews);
    }

    /// <summary>
    /// Get a review by its ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductReviewResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var review = await _reviewService.GetByIdAsync(id);
        if (review == null)
        {
            return NotFound(new { message = $"Review with ID '{id}' was not found." });
        }

        return Ok(review);
    }

    /// <summary>
    /// Submit a new product review (Authenticated Users)
    /// </summary>
    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(ProductReviewResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateProductReviewDto request)
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

        try
        {
            var created = await _reviewService.CreateReviewAsync(userId.Value, request);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating review for product {ProductId}", request.ProductId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while submitting your review." });
        }
    }

    /// <summary>
    /// Update a review (Author or Admin)
    /// </summary>
    [HttpPut("{id}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductReviewResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateProductReviewDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        Guid reviewId = Guid.TryParse(id, out var parsed) ? parsed : (request.ProductId ?? Guid.Empty);
        var userId = GetCurrentUserId() ?? Guid.Empty;
        var isAdmin = User.IsInRole("Admin");

        try
        {
            var updated = await _reviewService.UpdateReviewAsync(reviewId, userId, isAdmin, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Review with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating review {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating the review." });
        }
    }

    /// <summary>
    /// Delete a review (Author or Admin)
    /// </summary>
    [HttpDelete("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetCurrentUserId() ?? Guid.Empty;
        var isAdmin = User.IsInRole("Admin");

        try
        {
            var deleted = await _reviewService.DeleteReviewAsync(id, userId, isAdmin);
            if (!deleted)
            {
                return NotFound(new { message = $"Review with ID '{id}' was not found." });
            }

            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting review {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while deleting the review." });
        }
    }

    /// <summary>
    /// Moderation queue: List all reviews with approval status filtering (Admin only)
    /// </summary>
    [HttpGet("moderation")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<ProductReviewResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetModerationList([FromQuery] bool? isApproved, [FromQuery] Guid? productId)
    {
        var reviews = await _reviewService.GetAllReviewsAsync(isApproved, productId);
        return Ok(reviews);
    }

    /// <summary>
    /// Moderate a review: Approve or reject (Admin only)
    /// </summary>
    [HttpPut("{id:guid}/moderation")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductReviewResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Moderate(Guid id, [FromBody] ReviewModerationDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var moderated = await _reviewService.ModerateReviewAsync(id, request.IsApproved);
            if (moderated == null)
            {
                return NotFound(new { message = $"Review with ID '{id}' was not found." });
            }

            return Ok(moderated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error moderating review {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while moderating the review." });
        }
    }

    private Guid? GetCurrentUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var userId) ? userId : null;
    }
}
