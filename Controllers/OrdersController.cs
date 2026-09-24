using System.Security.Claims;
using Arboveya.Api.DTOs;
using Arboveya.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Arboveya.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orderService;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(IOrderService orderService, ILogger<OrdersController> logger)
    {
        _orderService = orderService;
        _logger = logger;
    }

    /// <summary>
    /// Place a new order (Authenticated User or Guest checkout)
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var currentUserId = GetCurrentUserId();

        try
        {
            var createdOrder = await _orderService.CreateOrderAsync(currentUserId, request);
            return CreatedAtAction(nameof(GetById), new { id = createdOrder.Id }, createdOrder);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error occurred during order checkout.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while placing your order." });
        }
    }

    /// <summary>
    /// Get order by ID (Customer can view their own order; Admin can view any)
    /// </summary>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id)
    {
        var currentUserId = GetCurrentUserId();
        var isAdmin = User.IsInRole("Admin");

        var order = await _orderService.GetByIdAsync(id, currentUserId, isAdmin);
        if (order == null)
        {
            return NotFound(new { message = $"Order with ID '{id}' was not found." });
        }

        return Ok(order);
    }

    /// <summary>
    /// Get all orders placed by the currently authenticated user or by email query
    /// </summary>
    [HttpGet("my-orders")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<OrderResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyOrders([FromQuery] string? email)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId.HasValue)
        {
            var orders = await _orderService.GetUserOrdersAsync(currentUserId.Value);
            if (orders.Any())
            {
                return Ok(orders);
            }
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var orders = await _orderService.GetOrdersByEmailAsync(email.Trim());
            return Ok(orders);
        }

        return Ok(Array.Empty<OrderResponseDto>());
    }

    /// <summary>
    /// Get all customer orders for products sold by a seller
    /// </summary>
    [HttpGet("seller-orders")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<OrderResponseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSellerOrders([FromQuery] Guid? sellerId)
    {
        var currentUserId = GetCurrentUserId();
        var targetSellerId = sellerId ?? currentUserId;

        if (targetSellerId.HasValue)
        {
            var sellerOrders = await _orderService.GetSellerOrdersAsync(targetSellerId.Value);
            if (sellerOrders.Any())
            {
                return Ok(sellerOrders);
            }
        }

        var allOrders = await _orderService.GetAllOrdersAsync();
        return Ok(allOrders);
    }

    /// <summary>
    /// Get all orders across the system (Admin only)
    /// </summary>
    [HttpGet]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(IEnumerable<OrderResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllOrders(
        [FromQuery] string? orderStatus,
        [FromQuery] string? paymentStatus,
        [FromQuery] string? search)
    {
        var orders = await _orderService.GetAllOrdersAsync(orderStatus, paymentStatus, search);
        return Ok(orders);
    }

    /// <summary>
    /// Update order status or payment status (Admin only)
    /// </summary>
    [HttpPut("{id:guid}/status")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateOrderStatus(Guid id, [FromBody] UpdateOrderStatusDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _orderService.UpdateOrderStatusAsync(id, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Order with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating order status for {OrderId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating the order status." });
        }
    }

    /// <summary>
    /// Update order shipping tracking number (Seller or Admin)
    /// </summary>
    [HttpPut("{id:guid}/tracking")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(OrderResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateOrderTracking(Guid id, [FromBody] UpdateOrderTrackingDto request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var updated = await _orderService.UpdateOrderTrackingAsync(id, request);
            if (updated == null)
            {
                return NotFound(new { message = $"Order with ID '{id}' was not found." });
            }

            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error updating tracking for order {OrderId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while updating order tracking." });
        }
    }

    private Guid? GetCurrentUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var userId) ? userId : null;
    }
}
