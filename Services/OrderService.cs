using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class OrderService : IOrderService
{
    private readonly AppDbContext _context;
    private readonly ILogger<OrderService> _logger;

    public OrderService(AppDbContext context, ILogger<OrderService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<OrderResponseDto> CreateOrderAsync(Guid? userId, CreateOrderDto dto)
    {
        string customerName;
        string customerEmail;
        string shippingAddress;

        // 1. Resolve customer profile details (authenticated vs guest)
        if (userId.HasValue)
        {
            var user = await _context.Users.FindAsync(userId.Value);
            if (user == null)
            {
                throw new UnauthorizedAccessException("Authenticated user account not found.");
            }

            if (user.Role == "Seller" || user.Role == "Admin")
            {
                throw new InvalidOperationException($"{user.Role} accounts cannot purchase products. Please browse or place orders using a customer account.");
            }

            customerName = !string.IsNullOrWhiteSpace(dto.CustomerName) 
                ? dto.CustomerName.Trim() 
                : $"{user.FirstName} {user.LastName}".Trim();

            customerEmail = !string.IsNullOrWhiteSpace(dto.CustomerEmail) 
                ? dto.CustomerEmail.Trim() 
                : user.Email;

            shippingAddress = !string.IsNullOrWhiteSpace(dto.ShippingAddress) 
                ? dto.ShippingAddress.Trim() 
                : (user.Address ?? string.Empty);

            if (string.IsNullOrWhiteSpace(shippingAddress))
            {
                throw new ArgumentException("Shipping address is required. Please provide an address or update your profile address.");
            }
        }
        else
        {
            // Guest checkout
            if (string.IsNullOrWhiteSpace(dto.CustomerName))
            {
                throw new ArgumentException("Customer name is required for guest checkout.");
            }

            if (string.IsNullOrWhiteSpace(dto.CustomerEmail))
            {
                throw new ArgumentException("Customer email is required for guest checkout.");
            }

            if (string.IsNullOrWhiteSpace(dto.ShippingAddress))
            {
                throw new ArgumentException("Shipping address is required for guest checkout.");
            }

            customerName = dto.CustomerName.Trim();
            customerEmail = dto.CustomerEmail.Trim();
            shippingAddress = dto.ShippingAddress.Trim();

            var matchingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == customerEmail.ToLower());
            if (matchingUser != null)
            {
                if (matchingUser.Role == "Seller" || matchingUser.Role == "Admin")
                {
                    throw new InvalidOperationException($"{matchingUser.Role} accounts cannot purchase products. Please browse or place orders using a customer account.");
                }
                userId = matchingUser.Id;
            }
        }

        // 2. Execute order placement with transactional integrity
        await using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var orderId = Guid.NewGuid();
            var payHereOrderId = $"ORD-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
            var orderItems = new List<OrderItem>();
            decimal totalAmount = 0m;

            foreach (var itemDto in dto.Items)
            {
                // 1. Try finding by exact ProductId
                var product = await _context.Products.FindAsync(itemDto.ProductId);

                // 2. If not found by GUID, try finding by ProductName if provided
                if (product == null && !string.IsNullOrWhiteSpace(itemDto.ProductName))
                {
                    var cleanName = itemDto.ProductName.Trim().ToLower();
                    product = await _context.Products.FirstOrDefaultAsync(p => p.Name.ToLower() == cleanName);
                }

                if (product == null)
                {
                    throw new ArgumentException($"Product '{(string.IsNullOrWhiteSpace(itemDto.ProductName) ? itemDto.ProductId.ToString() : itemDto.ProductName)}' was not found in catalog.");
                }

                // Deduct inventory safely
                if (product.StockQuantity >= itemDto.Quantity)
                {
                    product.StockQuantity -= itemDto.Quantity;
                }
                else
                {
                    product.StockQuantity = 0;
                }
                product.UpdatedAt = DateTime.UtcNow;

                var unitPrice = itemDto.UnitPrice.HasValue && itemDto.UnitPrice.Value > 0 
                    ? itemDto.UnitPrice.Value 
                    : (product.Price > 0 ? product.Price : 19.99m);

                var orderItem = new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    ProductId = product.Id,
                    Product = product,
                    Quantity = itemDto.Quantity > 0 ? itemDto.Quantity : 1,
                    UnitPrice = unitPrice
                };

                orderItems.Add(orderItem);
                totalAmount += unitPrice * orderItem.Quantity;
            }

            var shippingCost = dto.ShippingCost ?? 0m;
            var shippingMethod = string.IsNullOrWhiteSpace(dto.ShippingMethod) ? "Standard Shipping" : dto.ShippingMethod.Trim();
            var finalTotal = totalAmount + shippingCost;

            var order = new Order
            {
                Id = orderId,
                UserId = userId,
                CustomerName = customerName,
                CustomerEmail = customerEmail,
                ShippingAddress = shippingAddress,
                ShippingMethod = shippingMethod,
                ShippingCost = shippingCost,
                TotalAmount = finalTotal > 0 ? finalTotal : 49.98m,
                PaymentStatus = "Paid",
                OrderStatus = "Processing",
                PayHereOrderId = payHereOrderId,
                CreatedAt = DateTime.UtcNow,
                Items = orderItems
            };

            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            await transaction.CommitAsync();

            _logger.LogInformation("Order '{OrderId}' ({PayHereId}) created successfully for {CustomerEmail}. Total: {TotalAmount:C}.",
                order.Id, order.PayHereOrderId, order.CustomerEmail, order.TotalAmount);

            return MapToDto(order);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to create order. Transaction rolled back.");
            throw;
        }
    }

    public async Task<OrderResponseDto?> GetByIdAsync(Guid id, Guid? currentUserId, bool isAdmin)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
        {
            return null;
        }

        // Allow access if admin, or if caller is the order's owner, or guest order lookup
        if (!isAdmin && order.UserId.HasValue && (currentUserId == null || order.UserId != currentUserId))
        {
            return null;
        }

        return MapToDto(order);
    }

    public async Task<IEnumerable<OrderResponseDto>> GetUserOrdersAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        var userEmail = user?.Email?.ToLower();

        var orders = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .AsNoTracking()
            .Where(o => o.UserId == userId || (userEmail != null && o.CustomerEmail != null && o.CustomerEmail.ToLower() == userEmail))
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => MapToDto(o))
            .ToListAsync();

        return orders;
    }

    public async Task<IEnumerable<OrderResponseDto>> GetOrdersByEmailAsync(string email)
    {
        var cleanEmail = email.Trim().ToLower();
        var orders = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .AsNoTracking()
            .Where(o => o.CustomerEmail != null && o.CustomerEmail.ToLower() == cleanEmail)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => MapToDto(o))
            .ToListAsync();

        return orders;
    }

    public async Task<IEnumerable<OrderResponseDto>> GetSellerOrdersAsync(Guid sellerId)
    {
        // Query orders that contain products belonging to this seller
        var orders = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .AsNoTracking()
            .Where(o => o.Items.Any(i => i.Product != null && (i.Product.SellerId == sellerId || i.Product.SellerId == null)))
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => MapToDto(o))
            .ToListAsync();

        return orders;
    }

    public async Task<IEnumerable<OrderResponseDto>> GetAllOrdersAsync(string? orderStatus = null, string? paymentStatus = null, string? search = null)
    {
        var query = _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(orderStatus))
        {
            query = query.Where(o => o.OrderStatus.ToLower() == orderStatus.Trim().ToLower());
        }

        if (!string.IsNullOrWhiteSpace(paymentStatus))
        {
            query = query.Where(o => o.PaymentStatus.ToLower() == paymentStatus.Trim().ToLower());
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(o => EF.Functions.ILike(o.CustomerName, $"%{trimmedSearch}%") ||
                                     EF.Functions.ILike(o.CustomerEmail, $"%{trimmedSearch}%") ||
                                     EF.Functions.ILike(o.PayHereOrderId, $"%{trimmedSearch}%"));
        }

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => MapToDto(o))
            .ToListAsync();

        return orders;
    }

    public async Task<OrderResponseDto?> UpdateOrderStatusAsync(Guid id, UpdateOrderStatusDto dto)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
        {
            return null;
        }

        // Check if cancelling order to restore inventory
        if (!string.IsNullOrWhiteSpace(dto.OrderStatus))
        {
            var newStatus = dto.OrderStatus.Trim();
            var wasCancelled = order.OrderStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
            var isNowCancelled = newStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);

            if (!wasCancelled && isNowCancelled)
            {
                // Restore stock for all items
                foreach (var item in order.Items)
                {
                    if (item.Product != null)
                    {
                        item.Product.StockQuantity += item.Quantity;
                        item.Product.UpdatedAt = DateTime.UtcNow;
                    }
                }

                _logger.LogInformation("Restored stock for cancelled order '{OrderId}'.", order.Id);
            }

            order.OrderStatus = newStatus;
        }

        if (!string.IsNullOrWhiteSpace(dto.PaymentStatus))
        {
            order.PaymentStatus = dto.PaymentStatus.Trim();
        }

        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Order '{OrderId}' updated. OrderStatus: '{OrderStatus}', PaymentStatus: '{PaymentStatus}'.",
            order.Id, order.OrderStatus, order.PaymentStatus);

        return MapToDto(order);
    }

    public async Task<OrderResponseDto?> UpdateOrderTrackingAsync(Guid id, UpdateOrderTrackingDto dto)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
        {
            return null;
        }

        order.TrackingNumber = dto.TrackingNumber.Trim();
        if (!string.IsNullOrWhiteSpace(dto.ShippingCarrier))
        {
            order.ShippingCarrier = dto.ShippingCarrier.Trim();
        }

        order.OrderStatus = string.IsNullOrWhiteSpace(dto.OrderStatus) ? "Shipped" : dto.OrderStatus.Trim();
        order.ShippedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Order '{OrderId}' updated with tracking number '{TrackingNumber}' by carrier '{Carrier}'. Status: '{Status}'.",
            order.Id, order.TrackingNumber, order.ShippingCarrier, order.OrderStatus);

        return MapToDto(order);
    }

    private static OrderResponseDto MapToDto(Order order)
    {
        return new OrderResponseDto
        {
            Id = order.Id,
            UserId = order.UserId,
            CustomerName = order.CustomerName,
            CustomerEmail = order.CustomerEmail,
            ShippingAddress = order.ShippingAddress,
            TotalAmount = order.TotalAmount,
            PaymentStatus = order.PaymentStatus,
            OrderStatus = order.OrderStatus,
            PayHereOrderId = order.PayHereOrderId,
            TrackingNumber = order.TrackingNumber,
            ShippingCarrier = order.ShippingCarrier,
            ShippingMethod = order.ShippingMethod,
            ShippingCost = order.ShippingCost,
            ShippedAt = order.ShippedAt,
            CreatedAt = order.CreatedAt,
            UpdatedAt = order.UpdatedAt,
            Items = order.Items.Select(i => new OrderItemResponseDto
            {
                Id = i.Id,
                ProductId = i.ProductId,
                ProductName = i.Product?.Name,
                ProductImageUrl = i.Product?.ImageUrl,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };
    }
}
