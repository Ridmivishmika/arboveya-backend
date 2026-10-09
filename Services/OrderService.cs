using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class OrderService : IOrderService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrderService> _logger;
    private readonly IEmailService _emailService;

    private static readonly HashSet<string> RestrictedCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Asian countries
        "Sri Lanka", "India", "China", "Japan", "Pakistan", "Bangladesh", "Indonesia", "Philippines",
        "Vietnam", "Thailand", "Myanmar", "Malaysia", "Singapore", "Nepal", "Cambodia", "Laos",
        "Bhutan", "Maldives", "South Korea", "North Korea", "Korea", "Taiwan", "Hong Kong", "Mongolia",
        "Kazakhstan", "Uzbekistan", "Turkmenistan", "Kyrgyzstan", "Tajikistan", "Afghanistan",
        "Iran", "Iraq", "Saudi Arabia", "Yemen", "Syria", "Jordan", "United Arab Emirates", "UAE",
        "Israel", "Palestine", "Lebanon", "Oman", "Kuwait", "Qatar", "Bahrain", "Armenia", "Azerbaijan",
        "Georgia", "Macau", "Brunei", "Timor-Leste", "Asia",
        // African countries
        "Nigeria", "Ethiopia", "Egypt", "Democratic Republic of the Congo", "DR Congo", "Congo",
        "Tanzania", "South Africa", "Kenya", "Uganda", "Sudan", "Morocco", "Angola", "Mozambique",
        "Ghana", "Madagascar", "Ivory Coast", "Cote d'Ivoire", "Cameroon", "Niger", "Mali",
        "Burkina Faso", "Malawi", "Zambia", "Chad", "Somalia", "Senegal", "Zimbabwe", "Guinea",
        "Rwanda", "Benin", "Burundi", "Tunisia", "South Sudan", "Togo", "Sierra Leone", "Libya",
        "Liberia", "Central African Republic", "Mauritania", "Eritrea", "Namibia", "Gambia", "Botswana",
        "Gabon", "Lesotho", "Guinea-Bissau", "Equatorial Guinea", "Mauritius", "Eswatini", "Swaziland",
        "Djibouti", "Comoros", "Cape Verde", "Cabo Verde", "Sao Tome and Principe", "Seychelles", "Africa"
    };

    public OrderService(AppDbContext context, IConfiguration configuration, ILogger<OrderService> logger, IEmailService emailService)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _emailService = emailService;
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

        // Validate Country restriction: buyers only without Asian and African countries
        if (!string.IsNullOrWhiteSpace(dto.Country))
        {
            var cleanCountry = dto.Country.Trim();
            if (RestrictedCountries.Contains(cleanCountry))
            {
                throw new ArgumentException($"Orders cannot be delivered to '{cleanCountry}'. Arboveya serves buyers exclusively in countries outside Asia and Africa (Europe, North America, South America, and Oceania).");
            }
        }

        if (!string.IsNullOrWhiteSpace(shippingAddress))
        {
            foreach (var restrictedCountry in RestrictedCountries)
            {
                var addressParts = shippingAddress.Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim());
                if (addressParts.Any(part => string.Equals(part, restrictedCountry, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException($"Orders cannot be delivered to '{restrictedCountry}'. Arboveya serves buyers exclusively in countries outside Asia and Africa (Europe, North America, South America, and Oceania).");
                }
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
                // 1. Try finding by exact ProductId with Seller details
                var product = await _context.Products.Include(p => p.Seller).FirstOrDefaultAsync(p => p.Id == itemDto.ProductId);

                // 2. If not found by GUID, try finding by ProductName if provided
                if (product == null && !string.IsNullOrWhiteSpace(itemDto.ProductName))
                {
                    var cleanName = itemDto.ProductName.Trim().ToLower();
                    product = await _context.Products.Include(p => p.Seller).FirstOrDefaultAsync(p => p.Name.ToLower() == cleanName);
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
                PaymentStatus = "Pending",
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

            _logger.LogInformation("Order '{OrderId}' ({PayHereId}) created with status '{PaymentStatus}' for {CustomerEmail}. Total: {TotalAmount:C}. Waiting for payment confirmation before notifying seller(s).",
                order.Id, order.PayHereOrderId, order.PaymentStatus, order.CustomerEmail, order.TotalAmount);

            var resp = MapToDto(order);

            // Populate PayHere details for client-side checkout
            var merchantId = Environment.GetEnvironmentVariable("PAYHERE_MERCHANT_ID")
                ?? _configuration["PAYHERE_MERCHANT_ID"]
                ?? _configuration["PayHere:MerchantId"]
                ?? "1211149";
            if (merchantId == "your_payhere_merchant_id" || string.IsNullOrWhiteSpace(merchantId)) merchantId = "1211149";

            var merchantSecret = Environment.GetEnvironmentVariable("PAYHERE_MERCHANT_SECRET")
                ?? _configuration["PAYHERE_MERCHANT_SECRET"]
                ?? _configuration["PayHere:MerchantSecret"]
                ?? "4UPxLq74JtT4LUPxLq74JtT";
            if (merchantSecret == "your_payhere_merchant_secret" || string.IsNullOrWhiteSpace(merchantSecret)) merchantSecret = "4UPxLq74JtT4LUPxLq74JtT";

            var isSandbox = (Environment.GetEnvironmentVariable("PAYHERE_SANDBOX")
                ?? _configuration["PAYHERE_SANDBOX"]
                ?? _configuration["PayHere:IsSandbox"]
                ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);

            var currency = (Environment.GetEnvironmentVariable("PAYHERE_CURRENCY")
                ?? _configuration["PAYHERE_CURRENCY"]
                ?? _configuration["PayHere:Currency"]
                ?? "LKR").Trim().ToUpperInvariant();

            var returnUrl = Environment.GetEnvironmentVariable("PAYHERE_RETURN_URL")
                ?? _configuration["PAYHERE_RETURN_URL"]
                ?? _configuration["PayHere:ReturnUrl"];

            var cancelUrl = Environment.GetEnvironmentVariable("PAYHERE_CANCEL_URL")
                ?? _configuration["PAYHERE_CANCEL_URL"]
                ?? _configuration["PayHere:CancelUrl"];

            var notifyUrl = Environment.GetEnvironmentVariable("PAYHERE_NOTIFY_URL")
                ?? _configuration["PAYHERE_NOTIFY_URL"]
                ?? _configuration["PayHere:NotifyUrl"];

            var actionUrl = isSandbox 
                ? "https://sandbox.payhere.lk/pay/checkout" 
                : "https://www.payhere.lk/pay/checkout";

            var nameParts = customerName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var firstName = nameParts.Length > 0 ? nameParts[0].Trim() : "Valued";
            var lastName = nameParts.Length > 1 ? nameParts[1].Trim() : "Customer";

            var customerPhone = !string.IsNullOrWhiteSpace(dto.CustomerPhone)
                ? dto.CustomerPhone.Trim()
                : "0771234567";

            var customerCountry = !string.IsNullOrWhiteSpace(dto.Country)
                ? dto.Country.Trim()
                : "United States";

            var city = "Colombo";
            if (!string.IsNullOrWhiteSpace(shippingAddress))
            {
                var addrParts = shippingAddress.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (addrParts.Length > 1)
                {
                    city = addrParts[1].Trim();
                }
            }

            var payHereHash = GeneratePayHereHash(merchantId, order.PayHereOrderId, order.TotalAmount, currency, merchantSecret);

            resp.PayHereDetails = new PayHereCheckoutDetailsDto
            {
                Sandbox = isSandbox,
                ActionUrl = actionUrl,
                MerchantId = merchantId,
                OrderId = order.PayHereOrderId,
                Items = $"Arboveya Herbal Order ({order.Items.Count} item(s))",
                Amount = order.TotalAmount,
                Currency = currency,
                Hash = payHereHash,
                FirstName = firstName,
                LastName = lastName,
                Email = customerEmail,
                Phone = customerPhone,
                Address = shippingAddress,
                City = city,
                Country = customerCountry,
                DeliveryAddress = shippingAddress,
                DeliveryCity = city,
                DeliveryCountry = customerCountry,
                Custom1 = string.Empty,
                Custom2 = string.Empty,
                ReturnUrl = returnUrl,
                CancelUrl = cancelUrl,
                NotifyUrl = notifyUrl
            };

            return resp;
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
                    .ThenInclude(p => p!.Seller)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
        {
            return null;
        }

        var oldStatus = order.OrderStatus;
        var oldPaymentStatus = order.PaymentStatus;

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
            var newPaymentStatus = dto.PaymentStatus.Trim();
            if (!order.PaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase) && 
                newPaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase))
            {
                // If not already cancelled, cancel and restore stock
                if (!order.OrderStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var item in order.Items)
                    {
                        if (item.Product != null)
                        {
                            item.Product.StockQuantity += item.Quantity;
                            item.Product.UpdatedAt = DateTime.UtcNow;
                        }
                    }
                    order.OrderStatus = "Cancelled";
                }
            }
            order.PaymentStatus = newPaymentStatus;
        }

        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Order '{OrderId}' updated. OrderStatus: '{OrderStatus}', PaymentStatus: '{PaymentStatus}'.",
            order.Id, order.OrderStatus, order.PaymentStatus);

        // Notify seller only when order has been Delivered and Paid (funds cleared for disbursement)
        if (!oldStatus.Equals("Delivered", StringComparison.OrdinalIgnoreCase) &&
            order.OrderStatus.Equals("Delivered", StringComparison.OrdinalIgnoreCase) &&
            order.PaymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase))
        {
            await SendDeliveredOrderPayoutEligibilityNotificationAsync(order);
        }

        // Notify buyer & seller if order was marked Refunded
        if (!oldPaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase) &&
            order.PaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase))
        {
            await SendOrderRefundNotificationsAsync(order, order.TotalAmount, "Refund processed by platform");
        }

        return MapToDto(order);
    }

    public async Task<OrderResponseDto?> UpdateOrderTrackingAsync(Guid id, UpdateOrderTrackingDto dto)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
                    .ThenInclude(p => p!.Seller)
            .FirstOrDefaultAsync(o => o.Id == id);

        if (order == null)
        {
            return null;
        }

        var oldStatus = order.OrderStatus;

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

        // Notify seller if tracking update marked the order as Delivered
        if (!oldStatus.Equals("Delivered", StringComparison.OrdinalIgnoreCase) &&
            order.OrderStatus.Equals("Delivered", StringComparison.OrdinalIgnoreCase) &&
            order.PaymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase))
        {
            await SendDeliveredOrderPayoutEligibilityNotificationAsync(order);
        }

        return MapToDto(order);
    }

    public async Task<OrderResponseDto?> RefundOrderAsync(Guid orderId, string? reason = null, decimal? refundAmount = null)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
                    .ThenInclude(p => p!.Seller)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return null;

        if (order.PaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Order '{order.PayHereOrderId}' is already marked as Refunded.");
        }

        var amountToRefund = (refundAmount.HasValue && refundAmount.Value > 0) ? refundAmount.Value : order.TotalAmount;

        // Restore inventory for items if order wasn't previously cancelled
        if (!order.OrderStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in order.Items)
            {
                if (item.Product != null)
                {
                    item.Product.StockQuantity += item.Quantity;
                    item.Product.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        order.PaymentStatus = "Refunded";
        order.OrderStatus = "Cancelled";
        order.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Order '{OrderId}' successfully refunded (${RefundAmount:F2}). Reason: '{Reason}'.",
            order.Id, amountToRefund, reason ?? "Requested by customer");

        // Send refund notification emails to buyer and seller
        await SendOrderRefundNotificationsAsync(order, amountToRefund, reason ?? "Requested by customer");

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

    public async Task<bool> ProcessPayHereNotificationAsync(PayHereNotificationDto notification)
    {
        if (string.IsNullOrWhiteSpace(notification.order_id))
        {
            _logger.LogWarning("PayHere IPN received without order_id.");
            return false;
        }

        var merchantId = Environment.GetEnvironmentVariable("PAYHERE_MERCHANT_ID")
            ?? _configuration["PAYHERE_MERCHANT_ID"]
            ?? _configuration["PayHere:MerchantId"]
            ?? "1211149";
        if (merchantId == "your_payhere_merchant_id" || string.IsNullOrWhiteSpace(merchantId)) merchantId = "1211149";

        var merchantSecret = Environment.GetEnvironmentVariable("PAYHERE_MERCHANT_SECRET")
            ?? _configuration["PAYHERE_MERCHANT_SECRET"]
            ?? _configuration["PayHere:MerchantSecret"]
            ?? "4UPxLq74JtT4LUPxLq74JtT";
        if (merchantSecret == "your_payhere_merchant_secret" || string.IsNullOrWhiteSpace(merchantSecret)) merchantSecret = "4UPxLq74JtT4LUPxLq74JtT";

        // 1. Mandatory verification checks according to PayHere security guidelines
        if (string.IsNullOrWhiteSpace(notification.md5sig) || string.IsNullOrWhiteSpace(notification.payhere_amount))
        {
            _logger.LogWarning("PayHere IPN rejected: missing md5sig or payhere_amount for order '{OrderId}'.", notification.order_id);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(notification.merchant_id) && 
            !string.Equals(notification.merchant_id.Trim(), merchantId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("PayHere IPN Merchant ID mismatch for order '{OrderId}'. Expected: {Expected}, Received: {Received}.",
                notification.order_id, merchantId, notification.merchant_id);
            return false;
        }

        // 2. MD5 signature verification:
        // MD5(merchant_id + order_id + payhere_amount + payhere_currency + status_code + strtoupper(md5(merchant_secret)))
        var hashedSecret = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(merchantSecret)))
            .Replace("-", "").ToUpperInvariant();
        var rawSig = $"{merchantId}{notification.order_id}{notification.payhere_amount}{notification.payhere_currency}{notification.status_code}{hashedSecret}";
        var computedSig = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(rawSig)))
            .Replace("-", "").ToUpperInvariant();

        if (!string.Equals(computedSig, notification.md5sig, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("PayHere IPN MD5 signature mismatch for order '{OrderId}'. Expected: {Expected}, Received: {Received}.",
                notification.order_id, computedSig, notification.md5sig);
            return false;
        }

        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
                    .ThenInclude(p => p!.Seller)
            .FirstOrDefaultAsync(o => o.PayHereOrderId == notification.order_id);

        if (order == null)
        {
            _logger.LogWarning("Order with PayHereOrderId '{OrderId}' not found.", notification.order_id);
            return false;
        }

        // 3. Amount integrity check to prevent underpayment fraud
        if (decimal.TryParse(notification.payhere_amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var paidAmount))
        {
            if (notification.status_code == 2 && paidAmount < order.TotalAmount)
            {
                _logger.LogWarning("PayHere IPN payment amount mismatch for order '{OrderId}'. Expected at least {Expected}, received {Received}.",
                    order.Id, order.TotalAmount, paidAmount);
                order.PaymentStatus = "PartiallyPaid";
                order.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return false;
            }
        }

        if (notification.status_code == 2)
        {
            var alreadyPaid = string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);

            order.PaymentStatus = "Paid";
            order.OrderStatus = "Processing";
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogInformation("Order '{OrderId}' successfully marked Paid via PayHere IPN notification. PaymentId: {PaymentId}.",
                order.Id, notification.payment_id);

            if (!alreadyPaid)
            {
                await SendPaidOrderSellerNotificationsAsync(order);
            }
        }
        else if (notification.status_code == 0)
        {
            order.PaymentStatus = "Pending";
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogInformation("Order '{OrderId}' payment is Pending in PayHere. PaymentId: {PaymentId}.",
                order.Id, notification.payment_id);
        }
        else if (notification.status_code == -1)
        {
            order.PaymentStatus = "Cancelled";
            order.OrderStatus = "Cancelled";
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogWarning("Order '{OrderId}' payment was cancelled by customer.", order.Id);
        }
        else if (notification.status_code == -2)
        {
            order.PaymentStatus = "Failed";
            order.OrderStatus = "Cancelled";
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogWarning("Order '{OrderId}' payment failed with status message: {Message}.",
                order.Id, notification.status_message);
        }
        else if (notification.status_code == -3)
        {
            order.PaymentStatus = "Chargedback";
            order.OrderStatus = "Cancelled";
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogWarning("Order '{OrderId}' payment was chargedback.", order.Id);
        }

        return true;
    }

    public async Task<OrderResponseDto?> ConfirmOrderPaymentAsync(Guid orderId, string? paymentId)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
                    .ThenInclude(p => p!.Seller)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return null;

        var alreadyPaid = string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);

        order.PaymentStatus = "Paid";
        order.OrderStatus = "Processing";
        order.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Order '{OrderId}' payment confirmed by client callback. PaymentId: {PaymentId}.",
            order.Id, paymentId);

        if (!alreadyPaid)
        {
            await SendPaidOrderSellerNotificationsAsync(order);
        }

        return MapToDto(order);
    }

    private async Task SendPaidOrderSellerNotificationsAsync(Order order)
    {
        try
        {
            var items = order.Items;
            if (items == null || !items.Any() || items.Any(i => i.Product == null || i.Product.Seller == null))
            {
                items = await _context.OrderItems
                    .Where(i => i.OrderId == order.Id)
                    .Include(i => i.Product)
                        .ThenInclude(p => p!.Seller)
                    .ToListAsync();
            }

            var sellerGroups = items
                .Where(i => i.Product != null && i.Product.Seller != null && !string.IsNullOrWhiteSpace(i.Product.Seller.Email))
                .GroupBy(i => i.Product!.Seller!);

            foreach (var group in sellerGroups)
            {
                var seller = group.Key;
                var sellerItems = group.ToList();
                var sellerSubtotal = sellerItems.Sum(si => si.UnitPrice * si.Quantity);

                var itemsHtml = new StringBuilder();
                foreach (var item in sellerItems)
                {
                    var pName = item.Product?.Name ?? "Botanical Product";
                    itemsHtml.Append($@"
                        <tr style=""border-bottom: 1px solid #e5ede6;"">
                            <td style=""padding: 10px; font-weight: 500; color: #1c3f24;"">{System.Net.WebUtility.HtmlEncode(pName)}</td>
                            <td style=""padding: 10px; text-align: center; color: #556b59;"">{item.Quantity}</td>
                            <td style=""padding: 10px; text-align: right; color: #556b59;"">${item.UnitPrice:F2}</td>
                            <td style=""padding: 10px; text-align: right; font-weight: 600; color: #1c3f24;"">${(item.UnitPrice * item.Quantity):F2}</td>
                        </tr>");
                }

                var bankDetailsHtml = !string.IsNullOrWhiteSpace(seller.BankAccountNumber)
                    ? $@"<div style=""background: #edf5ee; border-left: 4px solid #24492d; padding: 12px 16px; border-radius: 6px; margin-top: 16px;"">
                            <strong style=""color: #1c3f24; font-size: 13px;"">Disbursement Bank Account:</strong>
                            <p style=""margin: 4px 0 0 0; font-size: 12px; color: #2e4d38; line-height: 1.5;"">
                                <strong>Bank:</strong> {System.Net.WebUtility.HtmlEncode(seller.BankName ?? "Registered Bank")}<br/>
                                <strong>Account Name:</strong> {System.Net.WebUtility.HtmlEncode(seller.BankAccountName ?? (seller.FirstName + " " + seller.LastName))}<br/>
                                <strong>Account Number:</strong> {System.Net.WebUtility.HtmlEncode(seller.BankAccountNumber)}<br/>
                                {(string.IsNullOrWhiteSpace(seller.BankBranch) ? "" : $"<strong>Branch / Swift:</strong> {System.Net.WebUtility.HtmlEncode(seller.BankBranch)}<br/>")}
                                Funds will be disbursed to your registered bank account according to standard vendor settlement cycles.
                            </p>
                         </div>"
                    : @"<div style=""background: #fffbeb; border-left: 4px solid #f59e0b; padding: 12px 16px; border-radius: 6px; margin-top: 16px;"">
                            <strong style=""color: #92400e; font-size: 13px;"">Bank Details Required:</strong>
                            <p style=""margin: 4px 0 0 0; font-size: 12px; color: #b45309; line-height: 1.5;"">
                                Please ensure your bank account details are up to date in your Seller Studio profile to receive automated vendor disbursements for this sale.
                            </p>
                         </div>";

                var emailSubject = $"[Arboveya] Payment Confirmed! New Order #{order.PayHereOrderId}";
                var emailHtml = $@"
                    <div style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; background: #ffffff; border: 1px solid #dbe6dc; border-radius: 12px; overflow: hidden;"">
                        <div style=""background: #1c3f24; padding: 24px; text-align: center;"">
                            <h1 style=""color: #ffffff; margin: 0; font-size: 24px; letter-spacing: 2px;"">ARBOVEYA</h1>
                            <p style=""color: #d6e8d8; margin: 4px 0 0 0; font-size: 12px; text-transform: uppercase; letter-spacing: 1px;"">Herbal & Botanical Marketplace</p>
                        </div>
                        <div style=""padding: 24px 28px;"">
                            <h2 style=""color: #1c3f24; margin: 0 0 12px 0; font-size: 18px;"">Payment Confirmed — Order Ready for Dispatch!</h2>
                            <p style=""color: #4b5563; font-size: 14px; line-height: 1.5; margin: 0 0 18px 0;"">
                                Hello <strong>{System.Net.WebUtility.HtmlEncode(seller.FirstName)}</strong>,<br/>
                                The buyer has successfully completed payment for an order containing botanical product(s) from your store.
                            </p>

                            <div style=""background: #f7faf7; border: 1px solid #e2eae2; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #374151; line-height: 1.6;"">
                                <div><strong>Order Reference:</strong> <span style=""font-family: monospace; color: #1c3f24;"">{order.PayHereOrderId}</span></div>
                                <div><strong>Payment Status:</strong> <span style=""font-weight: bold; color: #15803d;"">Paid (Verified)</span></div>
                                <div><strong>Order Date:</strong> {order.CreatedAt:MMMM dd, yyyy HH:mm} UTC</div>
                                <div><strong>Buyer:</strong> {System.Net.WebUtility.HtmlEncode(order.CustomerName)} ({System.Net.WebUtility.HtmlEncode(order.CustomerEmail)})</div>
                                <div><strong>Shipping Destination:</strong> {System.Net.WebUtility.HtmlEncode(order.ShippingAddress)}</div>
                                <div><strong>Shipping Method:</strong> {System.Net.WebUtility.HtmlEncode(order.ShippingMethod ?? "Standard Shipping")}</div>
                            </div>

                            <h3 style=""color: #1c3f24; font-size: 14px; margin: 0 0 10px 0; text-transform: uppercase; letter-spacing: 0.5px;"">Ordered Items</h3>
                            <table style=""width: 100%; border-collapse: collapse; font-size: 13px; margin-bottom: 16px;"">
                                <thead>
                                    <tr style=""background: #f0f5f1; color: #1c3f24; text-align: left;"">
                                        <th style=""padding: 10px; border-radius: 4px 0 0 4px;"">Item</th>
                                        <th style=""padding: 10px; text-align: center;"">Qty</th>
                                        <th style=""padding: 10px; text-align: right;"">Price</th>
                                        <th style=""padding: 10px; text-align: right; border-radius: 0 4px 4px 0;"">Subtotal</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {itemsHtml}
                                </tbody>
                                <tfoot>
                                    <tr>
                                        <td colspan=""3"" style=""padding: 12px 10px; text-align: right; font-weight: bold; color: #1c3f24;"">Seller Earnings:</td>
                                        <td style=""padding: 12px 10px; text-align: right; font-weight: bold; font-size: 15px; color: #1c3f24;"">${sellerSubtotal:F2}</td>
                                    </tr>
                                </tfoot>
                            </table>

                            {bankDetailsHtml}

                            <div style=""margin-top: 24px; text-align: center;"">
                                <p style=""font-size: 12px; color: #15803d; font-weight: 500; margin-bottom: 8px;"">Payment has been verified. Please prepare and dispatch the botanical remedies according to your handling schedule.</p>
                            </div>
                        </div>
                        <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                            &copy; {DateTime.UtcNow.Year} Arboveya Herbal Botanical. All rights reserved.
                        </div>
                    </div>";

                await _emailService.SendEmailAsync(seller.Email, emailSubject, emailHtml);
                _logger.LogInformation("Sent paid order notification email for order '{OrderId}' to seller '{SellerEmail}'.",
                    order.Id, seller.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send seller paid order notification email(s) for order '{OrderId}'.", order.Id);
        }
    }

    private async Task SendDeliveredOrderPayoutEligibilityNotificationAsync(Order order)
    {
        try
        {
            var items = order.Items;
            if (items == null || !items.Any() || items.Any(i => i.Product == null || i.Product.Seller == null))
            {
                items = await _context.OrderItems
                    .Where(i => i.OrderId == order.Id)
                    .Include(i => i.Product)
                        .ThenInclude(p => p!.Seller)
                    .ToListAsync();
            }

            var sellerGroups = items
                .Where(i => i.Product != null && i.Product.Seller != null && !string.IsNullOrWhiteSpace(i.Product.Seller.Email))
                .GroupBy(i => i.Product!.Seller!);

            foreach (var group in sellerGroups)
            {
                var seller = group.Key;
                var sellerItems = group.ToList();
                var sellerSubtotal = sellerItems.Sum(si => si.UnitPrice * si.Quantity);

                var bankInfo = !string.IsNullOrWhiteSpace(seller.BankAccountNumber)
                    ? $"{System.Net.WebUtility.HtmlEncode(seller.BankName ?? "Registered Bank")} (Account: {System.Net.WebUtility.HtmlEncode(seller.BankAccountNumber)})"
                    : "Registered Bank Account on file";

                var emailSubject = $"[Arboveya] Order Delivered! Payout Cleared for Order #{order.PayHereOrderId}";
                var emailHtml = $@"
                    <div style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; background: #ffffff; border: 1px solid #dbe6dc; border-radius: 12px; overflow: hidden;"">
                        <div style=""background: #1c3f24; padding: 24px; text-align: center;"">
                            <h1 style=""color: #ffffff; margin: 0; font-size: 24px; letter-spacing: 2px;"">ARBOVEYA</h1>
                            <p style=""color: #d6e8d8; margin: 4px 0 0 0; font-size: 12px; text-transform: uppercase; letter-spacing: 1px;"">Seller Payout Notification</p>
                        </div>
                        <div style=""padding: 24px 28px;"">
                            <h2 style=""color: #1c3f24; margin: 0 0 12px 0; font-size: 18px;"">Customer Delivery Confirmed — Funds Released!</h2>
                            <p style=""color: #4b5563; font-size: 14px; line-height: 1.5; margin: 0 0 18px 0;"">
                                Hello <strong>{System.Net.WebUtility.HtmlEncode(seller.FirstName)}</strong>,<br/>
                                Great news! The botanical package for order <strong>#{order.PayHereOrderId}</strong> has been successfully delivered to the customer.
                            </p>

                            <div style=""background: #edf5ee; border: 1px solid #c2dec5; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #1c3f24; line-height: 1.6;"">
                                <div><strong>Order Reference:</strong> <span style=""font-family: monospace;"">{order.PayHereOrderId}</span></div>
                                <div><strong>Delivery Status:</strong> <span style=""font-weight: bold; color: #15803d;"">Delivered</span></div>
                                <div><strong>Escrow Funds Status:</strong> <span style=""font-weight: bold; color: #15803d;"">Released (Cleared for Disbursement)</span></div>
                                <div><strong>Net Seller Earnings:</strong> <span style=""font-weight: bold; font-size: 15px;"">${sellerSubtotal:F2}</span></div>
                                <div><strong>Payout Destination:</strong> {bankInfo}</div>
                            </div>

                            <p style=""font-size: 13px; color: #4b5563; line-height: 1.5;"">
                                In accordance with Arboveya's seller settlement policy, funds are released immediately upon delivery confirmation and will be disbursed to your bank account during the next regular payout cycle.
                            </p>
                        </div>
                        <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                            &copy; {DateTime.UtcNow.Year} Arboveya Herbal Botanical. All rights reserved.
                        </div>
                    </div>";

                await _emailService.SendEmailAsync(seller.Email, emailSubject, emailHtml);
                _logger.LogInformation("Sent delivery payout clearance notification email for order '{OrderId}' to seller '{SellerEmail}'.",
                    order.Id, seller.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send seller payout clearance email for order '{OrderId}'.", order.Id);
        }
    }

    private async Task SendOrderRefundNotificationsAsync(Order order, decimal refundAmount, string reason)
    {
        try
        {
            // 1. Email to Buyer
            if (!string.IsNullOrWhiteSpace(order.CustomerEmail))
            {
                var buyerSubject = $"[Arboveya] Refund Confirmation — Order #{order.PayHereOrderId}";
                var buyerHtml = $@"
                    <div style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; background: #ffffff; border: 1px solid #dbe6dc; border-radius: 12px; overflow: hidden;"">
                        <div style=""background: #1c3f24; padding: 24px; text-align: center;"">
                            <h1 style=""color: #ffffff; margin: 0; font-size: 24px; letter-spacing: 2px;"">ARBOVEYA</h1>
                            <p style=""color: #d6e8d8; margin: 4px 0 0 0; font-size: 12px; text-transform: uppercase; letter-spacing: 1px;"">Refund Notice</p>
                        </div>
                        <div style=""padding: 24px 28px;"">
                            <h2 style=""color: #1c3f24; margin: 0 0 12px 0; font-size: 18px;"">Your Refund Has Been Processed</h2>
                            <p style=""color: #4b5563; font-size: 14px; line-height: 1.5; margin: 0 0 18px 0;"">
                                Hello <strong>{System.Net.WebUtility.HtmlEncode(order.CustomerName)}</strong>,<br/>
                                A refund has been processed for your order with Arboveya Herbal Botanical.
                            </p>

                            <div style=""background: #f7faf7; border: 1px solid #e2eae2; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #374151; line-height: 1.6;"">
                                <div><strong>Order Reference:</strong> <span style=""font-family: monospace; color: #1c3f24;"">{order.PayHereOrderId}</span></div>
                                <div><strong>Refund Amount:</strong> <strong style=""color: #15803d; font-size: 15px;"">${refundAmount:F2}</strong></div>
                                <div><strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(reason)}</div>
                                <div><strong>Payment Status:</strong> <span style=""color: #b91c1c; font-weight: bold;"">Refunded</span></div>
                            </div>

                            <p style=""font-size: 13px; color: #4b5563; line-height: 1.5;"">
                                Funds have been returned to your original payment card via PayHere. Depending on your bank's card processing cycle, it usually takes <strong>5 to 10 business days</strong> for the credit to appear on your statement.
                            </p>
                        </div>
                        <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                            &copy; {DateTime.UtcNow.Year} Arboveya Herbal Botanical. All rights reserved.
                        </div>
                    </div>";

                await _emailService.SendEmailAsync(order.CustomerEmail, buyerSubject, buyerHtml);
                _logger.LogInformation("Sent refund confirmation email for order '{OrderId}' to buyer '{BuyerEmail}'.", order.Id, order.CustomerEmail);
            }

            // 2. Email to Seller(s)
            var items = order.Items;
            if (items == null || !items.Any() || items.Any(i => i.Product == null || i.Product.Seller == null))
            {
                items = await _context.OrderItems
                    .Where(i => i.OrderId == order.Id)
                    .Include(i => i.Product)
                        .ThenInclude(p => p!.Seller)
                    .ToListAsync();
            }

            var sellerGroups = items
                .Where(i => i.Product != null && i.Product.Seller != null && !string.IsNullOrWhiteSpace(i.Product.Seller.Email))
                .GroupBy(i => i.Product!.Seller!);

            foreach (var group in sellerGroups)
            {
                var seller = group.Key;
                var sellerSubject = $"[Arboveya] Order Refund Notice — Order #{order.PayHereOrderId}";
                var sellerHtml = $@"
                    <div style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; background: #7f1d1d; border-radius: 12px; overflow: hidden;"">
                        <div style=""padding: 24px; text-align: center;"">
                            <h1 style=""color: #ffffff; margin: 0; font-size: 24px; letter-spacing: 2px;"">ARBOVEYA</h1>
                            <p style=""color: #fecaca; margin: 4px 0 0 0; font-size: 12px; text-transform: uppercase; letter-spacing: 1px;"">Order Refund Notice</p>
                        </div>
                        <div style=""background: #ffffff; padding: 24px 28px;"">
                            <h2 style=""color: #991b1b; margin: 0 0 12px 0; font-size: 18px;"">Order Refunded & Escrow Withheld</h2>
                            <p style=""color: #4b5563; font-size: 14px; line-height: 1.5; margin: 0 0 18px 0;"">
                                Hello <strong>{System.Net.WebUtility.HtmlEncode(seller.FirstName)}</strong>,<br/>
                                Please be informed that order <strong>#{order.PayHereOrderId}</strong> has been refunded to the customer.
                            </p>

                            <div style=""background: #fff5f5; border: 1px solid #fed7d7; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #7f1d1d; line-height: 1.6;"">
                                <div><strong>Order Reference:</strong> <span style=""font-family: monospace;"">{order.PayHereOrderId}</span></div>
                                <div><strong>Refund Reason:</strong> {System.Net.WebUtility.HtmlEncode(reason)}</div>
                                <div><strong>Escrow Status:</strong> <strong>Cancelled / Withheld</strong> (No payout will be disbursed)</div>
                                <div><strong>Inventory:</strong> Item stock has been automatically restored to your store catalog.</div>
                            </div>
                        </div>
                        <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                            &copy; {DateTime.UtcNow.Year} Arboveya Herbal Botanical. All rights reserved.
                        </div>
                    </div>";

                await _emailService.SendEmailAsync(seller.Email, sellerSubject, sellerHtml);
                _logger.LogInformation("Sent refund notice email for order '{OrderId}' to seller '{SellerEmail}'.", order.Id, seller.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send refund notification emails for order '{OrderId}'.", order.Id);
        }
    }

    private static string GeneratePayHereHash(string merchantId, string orderId, decimal amount, string currency, string merchantSecret)
    {
        var formattedAmount = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var hashedSecret = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(merchantSecret)))
            .Replace("-", "").ToUpperInvariant();

        var rawString = $"{merchantId}{orderId}{formattedAmount}{currency}{hashedSecret}";
        return BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(rawString)))
            .Replace("-", "").ToUpperInvariant();
    }
}
