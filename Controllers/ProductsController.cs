using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _productService;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(IProductService productService, ILogger<ProductsController> logger)
    {
        _productService = productService;
        _logger = logger;
    }

    /// <summary>
    /// Get all products with optional keyword, category, price, rating, sorting and pagination (Public)
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PaginatedProductsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? search, 
        [FromQuery] Guid? categoryId,
        [FromQuery] Guid? wellnessNeedId,
        [FromQuery] decimal? minPrice,
        [FromQuery] decimal? maxPrice,
        [FromQuery] int? minRating,
        [FromQuery] string? sortBy,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var products = await _productService.GetAllAsync(search, categoryId, wellnessNeedId, minPrice, maxPrice, minRating, sortBy, page, pageSize);
        return Ok(products);
    }

    /// <summary>
    /// Get related products for a product (Public)
    /// </summary>
    [HttpGet("{id:guid}/related")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<ProductResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRelated(Guid id, [FromQuery] int limit = 4)
    {
        var related = await _productService.GetRelatedAsync(id, limit);
        return Ok(related);
    }

    /// <summary>
    /// Get a product by its ID (Public)
    /// </summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var product = await _productService.GetByIdAsync(id);
        if (product == null)
        {
            return NotFound(new { message = $"Product with ID '{id}' was not found." });
        }

        return Ok(product);
    }

    /// <summary>
    /// Get a product by its unique Name (Public)
    /// </summary>
    [HttpGet("by-name/{name}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "Product name is required." });
        }

        var product = await _productService.GetByNameAsync(name);
        if (product == null)
        {
            return NotFound(new { message = $"Product with name '{name}' was not found." });
        }

        return Ok(product);
    }

    /// <summary>
    /// Create a new product (Admin only)
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateProductDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var created = await _productService.CreateAsync(request);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating product '{Name}'", request.Name);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while creating the product." });
        }
    }

    /// <summary>
    /// Update an existing product (Admin only)
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProductDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _productService.UpdateAsync(id, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Product with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating product '{Id}'", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating the product." });
        }
    }

    /// <summary>
    /// Delete a product (Admin only)
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
            var deleted = await _productService.DeleteAsync(id);
            if (!deleted)
            {
                return NotFound(new { message = $"Product with ID '{id}' was not found." });
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting product '{Id}'", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while deleting the product." });
        }
    }

    /// <summary>
    /// Upload product image (Admin / Manager)
    /// </summary>
    [HttpPost("upload-image")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadImage(IFormFile? file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "Please select an image file to upload." });
        }

        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".svg" };
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(extension))
        {
            return BadRequest(new { message = $"Unsupported file format '{extension}'. Allowed: jpg, jpeg, png, webp, avif, svg." });
        }

        if (file.Length > 10 * 1024 * 1024) // 10MB limit
        {
            return BadRequest(new { message = "Image size exceeds maximum limit of 10MB." });
        }

        var webRootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
        if (!Directory.Exists(webRootPath))
        {
            Directory.CreateDirectory(webRootPath);
        }

        var uniqueFileName = $"{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(webRootPath, uniqueFileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var relativeUrl = $"/uploads/{uniqueFileName}";
        return Ok(new { imageUrl = relativeUrl, fileName = uniqueFileName });
    }

    /// <summary>
    /// Submit a product as a Seller (Pending admin review)
    /// </summary>
    [HttpPost("seller")]
    [AllowAnonymous] // Allows demo submission or bearer token
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSellerProduct([FromBody] CreateSellerProductDto request, [FromQuery] Guid? sellerId)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            // If sellerId not supplied in query, find or use current seller
            var targetSellerId = sellerId ?? Guid.Empty;
            if (targetSellerId == Guid.Empty)
            {
                var sellerClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(sellerClaim) && Guid.TryParse(sellerClaim, out var claimId))
                {
                    targetSellerId = claimId;
                }
            }

            var created = await _productService.CreateSellerProductAsync(targetSellerId, request);
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error submitting seller product '{ProductName}'", request.Name);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while submitting the product." });
        }
    }

    /// <summary>
    /// Get products belonging to a seller
    /// </summary>
    [HttpGet("seller/my-products")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<ProductResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSellerProducts([FromQuery] Guid? sellerId)
    {
        var targetSellerId = sellerId ?? Guid.Empty;
        if (targetSellerId == Guid.Empty)
        {
            var sellerClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(sellerClaim) && Guid.TryParse(sellerClaim, out var claimId))
            {
                targetSellerId = claimId;
            }
        }

        var products = await _productService.GetSellerProductsAsync(targetSellerId);
        return Ok(products);
    }

    /// <summary>
    /// List products pending Admin approval (Admin only / demo allowed)
    /// </summary>
    [HttpGet("admin/pending")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<ProductResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingProducts()
    {
        var products = await _productService.GetPendingProductsAsync();
        return Ok(products);
    }

    /// <summary>
    /// Assign category and approve product (Admin only / demo allowed)
    /// </summary>
    [HttpPut("{id:guid}/approve")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveProduct(Guid id, [FromBody] ApproveProductDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _productService.ApproveProductAsync(id, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Product with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error approving product '{ProductId}'", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while approving the product." });
        }
    }

    /// <summary>
    /// Update a seller's product (Seller or Admin)
    /// </summary>
    [HttpPut("seller/{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ProductResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSellerProduct(Guid id, [FromBody] UpdateProductDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _productService.UpdateAsync(id, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Product with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating seller product '{ProductId}'", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating the product." });
        }
    }

    /// <summary>
    /// Delete a seller's product (Seller or Admin)
    /// </summary>
    [HttpDelete("seller/{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSellerProduct(Guid id)
    {
        try
        {
            var success = await _productService.DeleteAsync(id);
            if (!success)
            {
                return NotFound(new { message = $"Product with ID '{id}' was not found." });
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error deleting seller product '{ProductId}'", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while deleting the product." });
        }
    }
}
