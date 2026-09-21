using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IAuthService _authService;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AppDbContext context, IAuthService authService, ILogger<AdminController> logger)
    {
        _context = context;
        _authService = authService;
        _logger = logger;
    }

    /// <summary>
    /// Overview of sales, orders, and system metrics for the Admin Dashboard
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(AdminDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDashboardMetrics()
    {
        try
        {
            var totalSales = await _context.Orders
                .Where(o => o.PaymentStatus == "Paid" || o.OrderStatus == "Delivered")
                .SumAsync(o => (decimal?)o.TotalAmount) ?? 0m;

            var totalOrders = await _context.Orders.CountAsync();
            var pendingOrders = await _context.Orders.CountAsync(o => o.OrderStatus == "Pending");
            var processingOrders = await _context.Orders.CountAsync(o => o.OrderStatus == "Processing");
            var shippedOrders = await _context.Orders.CountAsync(o => o.OrderStatus == "Shipped");
            var deliveredOrders = await _context.Orders.CountAsync(o => o.OrderStatus == "Delivered");
            var cancelledOrders = await _context.Orders.CountAsync(o => o.OrderStatus == "Cancelled");

            var totalProducts = await _context.Products.CountAsync();
            var lowStockProducts = await _context.Products.CountAsync(p => p.StockQuantity <= 5);

            var totalCustomers = await _context.Users.CountAsync(u => u.Role == "Customer");

            var pendingReviews = await _context.ProductReviews.CountAsync(r => !r.IsApproved);
            var pendingBlogPosts = await _context.BlogPosts.CountAsync(b => !b.IsApproved);
            var pendingProducts = await _context.Products.CountAsync(p => p.ApprovalStatus == "Pending");
            var unreadMessages = await _context.ContactMessages.CountAsync(m => m.Status == "New" || m.Status == "Pending");

            var dashboard = new AdminDashboardDto
            {
                TotalSales = totalSales,
                TotalOrders = totalOrders,
                PendingOrdersCount = pendingOrders,
                ProcessingOrdersCount = processingOrders,
                ShippedOrdersCount = shippedOrders,
                DeliveredOrdersCount = deliveredOrders,
                CancelledOrdersCount = cancelledOrders,
                TotalProducts = totalProducts,
                LowStockProductsCount = lowStockProducts,
                TotalCustomers = totalCustomers,
                PendingReviewsCount = pendingReviews,
                PendingBlogPostsCount = pendingBlogPosts,
                PendingProductsCount = pendingProducts,
                UnreadContactMessagesCount = unreadMessages,
                Timestamp = DateTime.UtcNow
            };

            return Ok(dashboard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating admin dashboard metrics.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while loading dashboard metrics." });
        }
    }

    /// <summary>
    /// List all registered users with optional search filtering (Admin only)
    /// </summary>
    [HttpGet("users")]
    [ProducesResponseType(typeof(IEnumerable<UserResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllUsers([FromQuery] string? search)
    {
        try
        {
            var users = await _authService.GetAllUsersAsync(search);
            return Ok(users);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving user list.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while retrieving users." });
        }
    }

    /// <summary>
    /// Get a user by their unique ID (Admin only)
    /// </summary>
    [HttpGet("users/{id:guid}")]
    [ProducesResponseType(typeof(UserResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUserById(Guid id)
    {
        try
        {
            var user = await _authService.GetUserByIdAsync(id);
            if (user == null)
            {
                return NotFound(new { message = $"User with ID '{id}' was not found." });
            }

            return Ok(user);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving user '{UserId}'.", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while retrieving the user." });
        }
    }

    /// <summary>
    /// List all registered sellers with their approval status (Admin only)
    /// </summary>
    [HttpGet("sellers")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<UserResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSellers([FromQuery] bool? isApproved)
    {
        try
        {
            var query = _context.Users
                .AsNoTracking()
                .Where(u => u.Role == "Seller");

            if (isApproved.HasValue)
            {
                query = query.Where(u => u.IsSellerApproved == isApproved.Value);
            }

            var sellers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new UserResponseDto
                {
                    Id = u.Id,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    Email = u.Email,
                    Role = u.Role,
                    Address = u.Address,
                    Nationality = u.Nationality,
                    PhoneNumber = u.PhoneNumber,
                    IsSellerApproved = u.IsSellerApproved,
                    CreatedAt = u.CreatedAt
                })
                .ToListAsync();

            return Ok(sellers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving sellers list.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while retrieving sellers." });
        }
    }

    /// <summary>
    /// Approve or toggle approval for a seller profile (Admin only)
    /// </summary>
    [HttpPut("sellers/{id:guid}/approve")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(UserResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ApproveSeller(Guid id, [FromBody] ApproveSellerRequestDto? body)
    {
        try
        {
            var seller = await _context.Users.FirstOrDefaultAsync(u => u.Id == id && u.Role == "Seller");
            if (seller == null)
            {
                return NotFound(new { message = $"Seller with ID '{id}' was not found." });
            }

            seller.IsSellerApproved = body?.IsApproved ?? true;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Admin updated seller '{SellerEmail}' approval status to {IsApproved}.",
                seller.Email, seller.IsSellerApproved);

            return Ok(new UserResponseDto
            {
                Id = seller.Id,
                FirstName = seller.FirstName,
                LastName = seller.LastName,
                Email = seller.Email,
                Role = seller.Role,
                Address = seller.Address,
                Nationality = seller.Nationality,
                PhoneNumber = seller.PhoneNumber,
                IsSellerApproved = seller.IsSellerApproved,
                CreatedAt = seller.CreatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating seller '{SellerId}' approval status.", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while moderating seller." });
        }
    }
}

public class ApproveSellerRequestDto
{
    public bool IsApproved { get; set; } = true;
}
