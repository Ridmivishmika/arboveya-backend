using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Extensions;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class OrderService : IOrderService
{
    private readonly AppDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrderService> _logger;
    private readonly IEmailService _emailService;
    private readonly IHttpClientFactory? _httpClientFactory;

    // In-memory token cache for PayHere OAuth Access Token (valid for 599 seconds)
    private static string? _cachedAccessToken;
    private static DateTime _tokenExpiresAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _tokenSemaphore = new(1, 1);

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

    public OrderService(
        AppDbContext context, 
        IConfiguration configuration, 
        ILogger<OrderService> logger, 
        IEmailService emailService,
        IHttpClientFactory? httpClientFactory = null)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
        _emailService = emailService;
        _httpClientFactory = httpClientFactory;
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

                if (itemDto.Quantity <= 0)
                {
                    throw new InvalidOperationException($"Quantity for item '{(string.IsNullOrWhiteSpace(itemDto.ProductName) ? product.Name : itemDto.ProductName)}' must be greater than zero.");
                }

                // Reject purchase when inventory is insufficient
                if (product.StockQuantity < itemDto.Quantity)
                {
                    throw new InvalidOperationException($"Insufficient stock for '{product.Name}'. Only {product.StockQuantity} unit(s) available.");
                }

                product.StockQuantity -= itemDto.Quantity;
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
                    Quantity = itemDto.Quantity,
                    UnitPrice = unitPrice
                };

                orderItems.Add(orderItem);
                totalAmount += unitPrice * orderItem.Quantity;
            }

            var shippingCost = dto.ShippingCost ?? 0m;
            var shippingMethod = string.IsNullOrWhiteSpace(dto.ShippingMethod) ? "Standard Shipping" : dto.ShippingMethod.Trim();
            var finalTotal = totalAmount + shippingCost;

            if (finalTotal <= 0)
            {
                throw new InvalidOperationException("Invalid order total.");
            }

            var order = new Order
            {
                Id = orderId,
                UserId = userId,
                CustomerName = customerName,
                CustomerEmail = customerEmail,
                ShippingAddress = shippingAddress,
                ShippingMethod = shippingMethod,
                ShippingCost = shippingCost,
                TotalAmount = finalTotal,
                PaymentStatus = "Pending",
                OrderStatus = "Pending",
                PayHereOrderId = payHereOrderId,
                EstimatedDeliveryDate = DateTime.UtcNow.AddDays(7 + (shippingMethod != null && (shippingMethod.Contains("Express", StringComparison.OrdinalIgnoreCase) || shippingMethod.Contains("Priority", StringComparison.OrdinalIgnoreCase)) ? 2 : (shippingMethod != null && (shippingMethod.Contains("Free", StringComparison.OrdinalIgnoreCase) || shippingMethod.Contains("Economy", StringComparison.OrdinalIgnoreCase)) ? 7 : 5)) + 14),
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
            var merchantId = _configuration.ResolveEnv("PayHere:MerchantId", "PAYHERE_MERCHANT_ID", "PayHere__MerchantId");
            if (string.IsNullOrWhiteSpace(merchantId))
            {
                throw new InvalidOperationException("PayHere Merchant ID is not configured. Please set PAYHERE_MERCHANT_ID in environment variables.");
            }

            var merchantSecret = _configuration.ResolveEnv("PayHere:MerchantSecret", "PAYHERE_MERCHANT_SECRET", "PayHere__MerchantSecret");
            if (string.IsNullOrWhiteSpace(merchantSecret))
            {
                throw new InvalidOperationException("PayHere Merchant Secret is not configured. Please set PAYHERE_MERCHANT_SECRET in environment variables.");
            }

            var isSandboxStr = _configuration.ResolveEnv("PayHere:IsSandbox", "PAYHERE_SANDBOX", "PayHere__IsSandbox") ?? "true";
            var isSandbox = isSandboxStr.Equals("true", StringComparison.OrdinalIgnoreCase);

            var currency = (_configuration.ResolveEnv("PayHere:Currency", "PAYHERE_CURRENCY", "PayHere__Currency") ?? "USD").Trim().ToUpperInvariant();
            var returnUrl = _configuration.ResolveEnv("PayHere:ReturnUrl", "PAYHERE_RETURN_URL", "PayHere__ReturnUrl") ?? string.Empty;
            var cancelUrl = _configuration.ResolveEnv("PayHere:CancelUrl", "PAYHERE_CANCEL_URL", "PayHere__CancelUrl") ?? string.Empty;
            var notifyUrl = _configuration.ResolveEnv("PayHere:NotifyUrl", "PAYHERE_NOTIFY_URL", "PayHere__NotifyUrl") ?? string.Empty;

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
                Items = $"Arboveya Order ({order.Items.Count} item(s))",
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
            if (!string.Equals(order.PaymentStatus, newPaymentStatus, StringComparison.OrdinalIgnoreCase))
            {
                // Protect gateway-confirmed financial states from arbitrary manual edits
                if (order.PaymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                    order.PaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase) ||
                    order.PaymentStatus.Equals("PartiallyRefunded", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Cannot manually overwrite gateway-confirmed payment status '{order.PaymentStatus}'. " +
                        "Financial transaction statuses are managed through PayHere gateway verification and refund workflows.");
                }

                if (newPaymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Payment status cannot be manually marked as 'Paid'. Payment confirmation requires PayHere gateway verification.");
                }

                if (newPaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Payment status cannot be manually marked as 'Refunded'. Please use the Refund Order workflow to process refunds.");
                }

                if (newPaymentStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) && 
                    !order.OrderStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
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

                order.PaymentStatus = newPaymentStatus;
            }
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

        order.TrackingUrl = !string.IsNullOrWhiteSpace(dto.TrackingUrl)
            ? dto.TrackingUrl.Trim()
            : GenerateCarrierTrackingUrl(order.ShippingCarrier, order.TrackingNumber);

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

    public async Task<OrderResponseDto?> RequestRefundAsync(Guid orderId, CustomerRequestRefundDto dto, Guid? currentUserId, string? userEmail)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
                    .ThenInclude(p => p!.Seller)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return null;

        if (currentUserId.HasValue && order.UserId.HasValue && order.UserId.Value != currentUserId.Value)
        {
            if (string.IsNullOrWhiteSpace(userEmail) || !order.CustomerEmail.Equals(userEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("You are not authorized to request a refund for this order.");
            }
        }

        if (order.PaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This order has already been refunded.");
        }

        if (!order.PaymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refund can only be requested for orders with confirmed payment.");
        }

        if (order.RefundStatus.Equals("Requested", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A refund request is already pending review for this order.");
        }

        // Refund policy:
        // 1. If delivered: buyers can request a refund immediately (damaged items, returns, etc.)
        // 2. If NOT delivered at estimated delivery date: buyers must wait 2 weeks (14 days) past the estimated delivery date
        var isDelivered = order.OrderStatus.Equals("Delivered", StringComparison.OrdinalIgnoreCase);
        if (!isDelivered)
        {
            var estDeliveryDate = order.EstimatedDeliveryDate ?? order.CreatedAt.AddDays(21);
            var eligibleDate = estDeliveryDate.AddDays(14);
            if (DateTime.UtcNow < eligibleDate)
            {
                throw new InvalidOperationException(
                    $"For undelivered packages, refund requests open 2 weeks after the estimated delivery date (eligible starting {eligibleDate:MMM dd, yyyy}).");
            }
        }

        var fullReason = string.IsNullOrWhiteSpace(dto.Details)
            ? dto.Reason.Trim()
            : $"{dto.Reason.Trim()} - {dto.Details.Trim()}";

        order.RefundStatus = "Requested";
        order.RefundReason = fullReason;
        order.RefundRequestedAt = DateTime.UtcNow;
        order.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Refund requested for Order '{OrderId}'. Reason: '{Reason}'.", order.Id, fullReason);

        await SendRefundRequestSellerNotificationAsync(order, fullReason);

        return MapToDto(order);
    }

    public async Task<OrderResponseDto?> RefundOrderAsync(
        Guid orderId, 
        string? reason = null, 
        decimal? refundAmount = null, 
        string? payHereRefundId = null, 
        string? notes = null,
        bool autoRefundSandbox = true,
        string? paymentId = null,
        Guid? currentUserId = null,
        bool isAdmin = false)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
                    .ThenInclude(p => p!.Seller)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return null;

        // 1. Transaction Ownership Verification (Issue 2 & Fix D)
        if (!isAdmin)
        {
            if (!currentUserId.HasValue || !order.Items.Any(i => i.Product != null && i.Product.SellerId == currentUserId.Value))
            {
                throw new UnauthorizedAccessException("You are not authorized to process refunds for this order.");
            }
        }

        // 2. Existing refund verification (Fix D)
        if (order.PaymentStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase) ||
            order.RefundStatus.Equals("Refunded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Order '{order.PayHereOrderId}' is already marked as Refunded.");
        }

        // 3. Amount validation (Issue 4 & Fix D)
        if (refundAmount.HasValue)
        {
            if (refundAmount.Value <= 0)
            {
                throw new InvalidOperationException("Refund amount must be greater than zero.");
            }
            if (refundAmount.Value > order.TotalAmount)
            {
                throw new InvalidOperationException($"Refund amount (${refundAmount.Value:F2}) cannot exceed the total order amount of ${order.TotalAmount:F2}.");
            }
        }

        var amountToRefund = (refundAmount.HasValue && refundAmount.Value > 0) ? refundAmount.Value : order.TotalAmount;
        var isPartialRefund = amountToRefund < order.TotalAmount;
        var effectivePaymentId = !string.IsNullOrWhiteSpace(paymentId) ? paymentId.Trim() : order.PaymentId;

        // 4. Duplicate request check if refund already pending on order
        if (order.RefundStatus.Equals("RefundPending", StringComparison.OrdinalIgnoreCase) ||
            order.RefundStatus.Equals("RefundProcessing", StringComparison.OrdinalIgnoreCase))
        {
            var verified = await RetrievePayHerePaymentAsync(order.Id, currentUserId, isAdmin);
            if (verified != null && string.Equals(verified.Status, "REFUNDED", StringComparison.OrdinalIgnoreCase))
            {
                return MapToDto(order);
            }
            throw new InvalidOperationException(
                "A refund request is currently pending or processing with PayHere for this order. " +
                "Please check PayHere payment status using 'Check Live PayHere Status' before submitting another refund.");
        }

        // 5. Payment status validation
        if (!order.PaymentStatus.Equals("Paid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refund can only be processed on orders with confirmed payment (current status: '{order.PaymentStatus}').");
        }

        // 6. Manual refund authorization check (Issue 6)
        var isManualRefund = !autoRefundSandbox || !string.IsNullOrWhiteSpace(payHereRefundId);
        if (isManualRefund)
        {
            if (!isAdmin)
            {
                throw new UnauthorizedAccessException("Only administrators are permitted to record manual/external refunds.");
            }
            if (string.IsNullOrWhiteSpace(payHereRefundId) && string.IsNullOrWhiteSpace(notes))
            {
                throw new InvalidOperationException("Manual refund recording requires a valid PayHere Refund Reference ID or external confirmation note as evidence.");
            }
        }

        // 7. Automated PayHere Refund processing
        string? executionResultStatus = null;
        if (!isManualRefund)
        {
            var refundResult = await ExecutePayHereRefundAsync(order, amountToRefund, isPartialRefund, reason ?? "Automated refund processed via PayHere", effectivePaymentId);
            payHereRefundId = refundResult.RefundReference;
            executionResultStatus = refundResult.PayHereStatus;
            if (!string.IsNullOrWhiteSpace(refundResult.DiscoveredPaymentId))
            {
                effectivePaymentId = refundResult.DiscoveredPaymentId;
                order.PaymentId = effectivePaymentId;
            }
        }

        // Persist paymentId if provided/resolved so future queries track PayHere payment identifier
        if (!string.IsNullOrWhiteSpace(effectivePaymentId) && string.IsNullOrWhiteSpace(order.PaymentId))
        {
            order.PaymentId = effectivePaymentId;
        }

        var parts = new List<string>();
        if (!isManualRefund)
        {
            parts.Add(isPartialRefund ? $"PayHere Partial Refund (${amountToRefund:F2})" : "PayHere Auto-Refund");
        }
        else
        {
            parts.Add(isPartialRefund ? $"Manual Partial Refund (${amountToRefund:F2})" : "Manual refund via PayHere");
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            parts.Add(reason.Trim());
        }

        if (!string.IsNullOrWhiteSpace(payHereRefundId))
        {
            parts.Add($"PayHere Ref: {payHereRefundId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            parts.Add($"Note: {notes.Trim()}");
        }

        order.RefundReason = string.Join(" | ", parts);
        order.UpdatedAt = DateTime.UtcNow;

        // 8. Update database according to PayHere status (Fix B, Issue 3 & Issue 4)
        if (isManualRefund || string.Equals(executionResultStatus, "REFUNDED", StringComparison.OrdinalIgnoreCase))
        {
            if (isPartialRefund)
            {
                order.PaymentStatus = "PartiallyRefunded";
                order.RefundStatus = "PartiallyRefunded";
            }
            else
            {
                order.PaymentStatus = "Refunded";
                order.OrderStatus = "Cancelled";
                order.RefundStatus = "Refunded";

                // Restore inventory for items if order wasn't previously cancelled
                foreach (var item in order.Items)
                {
                    if (item.Product != null)
                    {
                        item.Product.StockQuantity += item.Quantity;
                        item.Product.UpdatedAt = DateTime.UtcNow;
                    }
                }
            }

            order.RefundProcessedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order '{OrderId}' successfully refunded (${RefundAmount:F2}) via PayHere (Auto: {AutoSandbox}). Status: {RefundStatus}. Reason: '{Reason}'.",
                order.Id, amountToRefund, !isManualRefund, order.RefundStatus, order.RefundReason);

            // Send refund notification emails to buyer and seller
            await SendOrderRefundNotificationsAsync(order, amountToRefund, order.RefundReason, payHereRefundId);
        }
        else if (string.Equals(executionResultStatus, "REFUND PROCESSING", StringComparison.OrdinalIgnoreCase))
        {
            order.RefundStatus = "RefundProcessing";
            await _context.SaveChangesAsync();
            _logger.LogInformation("Order '{OrderId}' refund is processing on PayHere. Status set to RefundProcessing.", order.Id);
        }
        else
        {
            // Status: 1 or REFUND REQUESTED: Pending gateway settlement
            order.RefundStatus = "RefundPending";
            await _context.SaveChangesAsync();
            _logger.LogInformation("Order '{OrderId}' refund request accepted by PayHere. Status set to RefundPending.", order.Id);
        }

        return MapToDto(order);
    }

    /// <summary>
    /// Retrieves or reuses cached PayHere OAuth Access Token using App ID and App Secret.
    /// Follows PayHere documentation Step 2 (Base64 authorization code) & Step 3 (Retrieve Access Token).
    /// </summary>
    private async Task<string> GetPayHereAccessTokenAsync(HttpClient client, string baseUrl, string appId, string appSecret, bool forceRefresh = false)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_cachedAccessToken) && DateTime.UtcNow < _tokenExpiresAt)
        {
            return _cachedAccessToken;
        }

        await _tokenSemaphore.WaitAsync();
        try
        {
            if (!forceRefresh && !string.IsNullOrWhiteSpace(_cachedAccessToken) && DateTime.UtcNow < _tokenExpiresAt)
            {
                return _cachedAccessToken;
            }

            // Step 2 & 3: Generate Base64 authorization code and request OAuth Bearer Token
            var tokenUrl = $"{baseUrl}/merchant/v1/oauth/token";
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
            var basicAuthBytes = Encoding.UTF8.GetBytes($"{appId.Trim()}:{appSecret.Trim()}");
            tokenRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(basicAuthBytes));
            tokenRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" }
            });

            var tokenResponse = await client.SendAsync(tokenRequest);
            var tokenJson = await tokenResponse.Content.ReadAsStringAsync();

            if (!tokenResponse.IsSuccessStatusCode)
            {
                _logger.LogError("[PayHere OAuth Failure] Status: {StatusCode}, Body: {Body}", tokenResponse.StatusCode, tokenJson);
                try
                {
                    using var errDoc = System.Text.Json.JsonDocument.Parse(tokenJson);
                    if (errDoc.RootElement.TryGetProperty("msg", out var msgElem))
                    {
                        var msgText = msgElem.GetString();
                        if (!string.IsNullOrWhiteSpace(msgText))
                        {
                            throw new InvalidOperationException(
                                $"PayHere OAuth Failed: {msgText}. Please sign in to PayHere Dashboard (https://sandbox.payhere.lk or https://payhere.lk), " +
                                "navigate to Settings > API Keys, ensure your API Key has the 'Automated Charging API' permission ticked, and set Allowed Domains to '*' for Sandbox.");
                        }
                    }
                    if (errDoc.RootElement.TryGetProperty("error_description", out var descElem))
                    {
                        throw new InvalidOperationException($"PayHere OAuth authentication failed: {descElem.GetString()}");
                    }
                    if (errDoc.RootElement.TryGetProperty("error", out var errElem))
                    {
                        throw new InvalidOperationException($"PayHere OAuth authentication failed: {errElem.GetString()}");
                    }
                }
                catch (System.Text.Json.JsonException) { }

                throw new InvalidOperationException(
                    $"PayHere OAuth authentication failed (HTTP {tokenResponse.StatusCode}). " +
                    "Please verify your PayHere App ID and App Secret in Settings > API Keys, ensure the 'Automated Charging API' permission is checked, and set Allowed Domains to '*' for Sandbox.");
            }

            using var tokenDoc = System.Text.Json.JsonDocument.Parse(tokenJson);
            if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessTokenElem) || string.IsNullOrWhiteSpace(accessTokenElem.GetString()))
            {
                throw new InvalidOperationException("PayHere did not return a valid OAuth access token.");
            }

            var accessToken = accessTokenElem.GetString()!;
            var expiresInSeconds = 599;
            if (tokenDoc.RootElement.TryGetProperty("expires_in", out var expiresElem) && expiresElem.TryGetInt32(out var sec))
            {
                expiresInSeconds = sec;
            }

            // Cache token safely with a 30-second buffer
            _cachedAccessToken = accessToken;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - 30));

            return accessToken;
        }
        finally
        {
            _tokenSemaphore.Release();
        }
    }

    private sealed class PayHereRefundExecutionResult
    {
        public string RefundReference { get; set; } = string.Empty;
        public string PayHereStatus { get; set; } = "ACCEPTED";
        public string? DiscoveredPaymentId { get; set; }
    }

    /// <summary>
    /// Matches the actual payment transaction from PayHere Retrieval API results.
    /// Evaluates stored payment_id, order_id reference, currency, amount, and successful status.
    /// </summary>
    private static JsonElement? FindMatchingPayment(
        JsonElement.ArrayEnumerator payments, 
        Order order, 
        string? expectedPaymentId)
    {
        JsonElement? bestMatch = null;
        int bestScore = -1;

        foreach (var p in payments)
        {
            int score = 0;

            // 1. payment_id match
            string? pid = null;
            if (p.TryGetProperty("payment_id", out var pidElem))
            {
                pid = pidElem.ValueKind == JsonValueKind.Number
                    ? pidElem.GetInt64().ToString()
                    : pidElem.GetString()?.Trim();
            }

            if (!string.IsNullOrWhiteSpace(expectedPaymentId) && 
                string.Equals(pid, expectedPaymentId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }

            // 2. order_id match
            if (p.TryGetProperty("order_id", out var oidElem))
            {
                var oid = oidElem.GetString()?.Trim();
                if (string.Equals(oid, order.PayHereOrderId, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
            }

            // 3. status match (prioritize successful and refundable states)
            if (p.TryGetProperty("status", out var statElem))
            {
                var stat = statElem.GetString()?.Trim().ToUpperInvariant() ?? "";
                if (stat == "RECEIVED" || stat == "SUCCESS" || stat == "COMPLETED" || stat == "PAID")
                {
                    score += 30;
                }
                else if (stat == "REFUNDED" || stat == "REFUND PROCESSING" || stat == "REFUND REQUESTED")
                {
                    score += 25;
                }
                else if (stat == "PENDING" || stat == "0")
                {
                    score += 5;
                }
            }

            // 4. amount match
            if (p.TryGetProperty("amount", out var amtElem))
            {
                decimal amt = 0;
                if (amtElem.ValueKind == JsonValueKind.Number && amtElem.TryGetDecimal(out var nAmt))
                {
                    amt = nAmt;
                }
                else if (decimal.TryParse(amtElem.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pAmt))
                {
                    amt = pAmt;
                }

                if (Math.Abs(amt - order.TotalAmount) < 0.05m)
                {
                    score += 20;
                }
            }

            // 5. currency match
            if (p.TryGetProperty("currency", out var curElem))
            {
                var cur = curElem.GetString()?.Trim().ToUpperInvariant();
                if (cur == "USD" || cur == "LKR")
                {
                    score += 10;
                }
            }

            // Must score at least 50 (must match order_id or payment_id)
            if (score > bestScore && score >= 50)
            {
                bestScore = score;
                bestMatch = p;
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// Executes automated PayHere Refund.
    /// Strictly adheres to official PayHere Retrieval & Refund API specifications:
    /// 1. Authenticates via OAuth 2.0 Client Credentials (/merchant/v1/oauth/token)
    /// 2. If payment_id is missing or unverified, consumes PayHere Retrieval API (/merchant/v1/payment/search?order_id=...)
    ///    to validate real PayHere payment_id, currency, amount, and card payment method
    /// 3. Submits refund request (POST /merchant/v1/payment/refund) with real payment_id and description (and amount if partial)
    /// 4. Checks PayHere response status code strictly (status: 1 means refund request accepted)
    /// 5. Confirms final payment status via Retrieval API
    /// </summary>
    private async Task<PayHereRefundExecutionResult> ExecutePayHereRefundAsync(
        Order order, 
        decimal amount, 
        bool isPartialRefund, 
        string reason, 
        string? paymentId)
    {
        var isSandboxStr = _configuration.ResolveEnv("PayHere:IsSandbox", "PAYHERE_SANDBOX", "PayHere__IsSandbox") ?? "true";
        var isSandbox = isSandboxStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        var appId = _configuration.ResolveEnv("PayHere:AppId", "PAYHERE_APP_ID", "PayHere__AppId");
        var appSecret = _configuration.ResolveEnv("PayHere:AppSecret", "PAYHERE_APP_SECRET", "PayHere__AppSecret");
        var baseUrl = isSandbox ? "https://sandbox.payhere.lk" : "https://www.payhere.lk";

        // Issue 1: Do not generate fake simulated sandbox refunds when credentials are missing
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            throw new InvalidOperationException(
                "PayHere AppId and AppSecret are not configured in the server environment. " +
                "To process refunds via PayHere, obtain your App ID and App Secret from PayHere Dashboard (Settings > API Keys) and configure PAYHERE_APP_ID and PAYHERE_APP_SECRET.");
        }

        var client = _httpClientFactory != null ? _httpClientFactory.CreateClient() : new HttpClient();

        // 1. Retrieve or refresh OAuth Access Token
        var accessToken = await GetPayHereAccessTokenAsync(client, baseUrl, appId, appSecret);

        // 2. Identify the payment via PayHere Retrieval API (/merchant/v1/payment/search?order_id=...)
        string? discoveredPaymentId = paymentId;
        string? livePaymentStatus = null;

        _logger.LogInformation("[PayHere Retrieval API] Querying payment search for order '{OrderId}' ({PayHereOrderId})...",
            order.Id, order.PayHereOrderId);

        var searchUrl = $"{baseUrl}/merchant/v1/payment/search?order_id={Uri.EscapeDataString(order.PayHereOrderId)}";
        var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        searchRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var searchResponse = await client.SendAsync(searchRequest);
        var searchContent = await searchResponse.Content.ReadAsStringAsync();

        if (searchResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            searchContent.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
            searchContent.Contains("token expired", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[PayHere Retrieval API] Token expired during payment search. Refreshing OAuth token and retrying...");
            _cachedAccessToken = null;
            _tokenExpiresAt = DateTime.MinValue;

            accessToken = await GetPayHereAccessTokenAsync(client, baseUrl, appId, appSecret, forceRefresh: true);
            searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            searchResponse = await client.SendAsync(searchRequest);
            searchContent = await searchResponse.Content.ReadAsStringAsync();
        }

        _logger.LogInformation("[PayHere Retrieval API Response] Status: {StatusCode}, Body: {Body}",
            searchResponse.StatusCode, searchContent);

        if (!searchResponse.IsSuccessStatusCode)
        {
            if (searchResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException(
                    "PayHere API Access Denied / Unauthorized: Check your PayHere Settings > API Keys. " +
                    "Ensure 'Automated Charging API' permission is enabled and Allowed Domains is set correctly.");
            }
            throw new InvalidOperationException($"PayHere Retrieval API failed with HTTP {searchResponse.StatusCode}: {searchContent}");
        }

        using var searchDoc = JsonDocument.Parse(searchContent);
        var sRoot = searchDoc.RootElement;
        var sCode = sRoot.TryGetProperty("status", out var sc) ? sc.GetInt32() : -1;

        if (sCode == -1)
        {
            var sMsg = sRoot.TryGetProperty("msg", out var sm) ? sm.GetString() : "No payments found";
            throw new InvalidOperationException($"PayHere Retrieval API returned '{sMsg}' for Order Reference '{order.PayHereOrderId}'.");
        }
        if (sCode == 0)
        {
            throw new InvalidOperationException($"PayHere payment for order '{order.PayHereOrderId}' is currently Pending (status: 0). Refunds can only be processed on confirmed successful payments.");
        }
        if (sCode == -2)
        {
            throw new InvalidOperationException($"PayHere payment for order '{order.PayHereOrderId}' was declined or failed authentication (status: -2). No successful payment available to refund.");
        }

        if (sCode != 1 || !sRoot.TryGetProperty("data", out var dataArr) ||
            dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"No payment records returned by PayHere Retrieval API for order '{order.PayHereOrderId}'.");
        }

        // Issue 5: Match the actual transaction from data array rather than choosing data[0]
        var matchedPayment = FindMatchingPayment(dataArr.EnumerateArray(), order, discoveredPaymentId);
        if (matchedPayment == null)
        {
            throw new InvalidOperationException(
                $"PayHere Retrieval API returned {dataArr.GetArrayLength()} transaction(s), but none matched order reference '{order.PayHereOrderId}' with amount ${order.TotalAmount:F2} and valid payment status.");
        }

        var matchedElem = matchedPayment.Value;
        if (matchedElem.TryGetProperty("payment_id", out var pidElem))
        {
            discoveredPaymentId = pidElem.ValueKind == JsonValueKind.Number
                ? pidElem.GetInt64().ToString()
                : pidElem.GetString()?.Trim();
        }

        livePaymentStatus = matchedElem.TryGetProperty("status", out var stElem) ? stElem.GetString()?.Trim().ToUpperInvariant() : null;

        // Check payment method: PayHere only supports card payment methods for automated refund
        if (matchedElem.TryGetProperty("payment_method", out var pmElem) &&
            pmElem.TryGetProperty("method", out var methElem))
        {
            var method = methElem.GetString() ?? "";
            if (method.Contains("FRIMI", StringComparison.OrdinalIgnoreCase) ||
                method.Contains("GENIE", StringComparison.OrdinalIgnoreCase) ||
                method.Contains("EZCASH", StringComparison.OrdinalIgnoreCase) ||
                method.Contains("MCASH", StringComparison.OrdinalIgnoreCase) ||
                method.Contains("VISHWA", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PayHere automated refund only supports card payments (Visa, Mastercard, American Express). " +
                    $"This payment was made via '{method}'. Please refund the customer manually via bank transfer.");
            }
        }

        // Check if payment has ALREADY been refunded on PayHere
        if (string.Equals(livePaymentStatus, "REFUNDED", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[PayHere Retrieval API] Payment '{PaymentId}' for order '{OrderId}' is already marked 'REFUNDED' on PayHere.",
                discoveredPaymentId, order.PayHereOrderId);
            return new PayHereRefundExecutionResult
            {
                RefundReference = $"PH-REFUNDED-{discoveredPaymentId ?? DateTime.UtcNow.ToString("yyyyMMdd")}",
                PayHereStatus = "REFUNDED",
                DiscoveredPaymentId = discoveredPaymentId
            };
        }

        // Check if refund is currently requested or processing on PayHere
        if (string.Equals(livePaymentStatus, "REFUND REQUESTED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(livePaymentStatus, "REFUND PROCESSING", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[PayHere Retrieval API] Order '{OrderId}' refund is already in status '{Status}' on PayHere.",
                order.PayHereOrderId, livePaymentStatus);
            return new PayHereRefundExecutionResult
            {
                RefundReference = $"PH-IN-PROGRESS-{discoveredPaymentId ?? DateTime.UtcNow.ToString("yyyyMMdd")}",
                PayHereStatus = livePaymentStatus!,
                DiscoveredPaymentId = discoveredPaymentId
            };
        }

        if (string.IsNullOrWhiteSpace(discoveredPaymentId))
        {
            throw new InvalidOperationException(
                $"Could not resolve PayHere payment_id for order '{order.PayHereOrderId}'.");
        }

        // 3. Build and execute PayHere Refund API request (POST /merchant/v1/payment/refund) (Fix C)
        var refundUrl = $"{baseUrl}/merchant/v1/payment/refund";
        var refundPayload = new Dictionary<string, object>
        {
            { "payment_id", discoveredPaymentId.Trim() },
            { "description", string.IsNullOrWhiteSpace(reason) ? $"Refund for order {order.PayHereOrderId}" : reason.Trim() }
        };

        if (isPartialRefund)
        {
            refundPayload["amount"] = amount.ToString("F2", CultureInfo.InvariantCulture);
        }

        var refundRequest = new HttpRequestMessage(HttpMethod.Post, refundUrl);
        refundRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        refundRequest.Content = new StringContent(
            JsonSerializer.Serialize(refundPayload),
            Encoding.UTF8,
            "application/json");

        var refundResponse = await client.SendAsync(refundRequest);
        var refundContent = await refundResponse.Content.ReadAsStringAsync();

        // Refresh token retry if needed
        if (refundResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            refundContent.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
            refundContent.Contains("token expired", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[PayHere API] Token expired. Refreshing OAuth Access Token and retrying refund...");
            _cachedAccessToken = null;
            _tokenExpiresAt = DateTime.MinValue;

            accessToken = await GetPayHereAccessTokenAsync(client, baseUrl, appId, appSecret, forceRefresh: true);
            refundRequest = new HttpRequestMessage(HttpMethod.Post, refundUrl);
            refundRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            refundRequest.Content = new StringContent(
                JsonSerializer.Serialize(refundPayload),
                Encoding.UTF8,
                "application/json");

            refundResponse = await client.SendAsync(refundRequest);
            refundContent = await refundResponse.Content.ReadAsStringAsync();
        }

        _logger.LogInformation("[PayHere Refund API Response] Status: {StatusCode}, Body: {Body}",
            refundResponse.StatusCode, refundContent);

        // Fix A & Issue 1: Never fake success on failure! Reject strictly and preserve the actual response.
        if (!refundResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PayHere refund failed. HTTP: {(int)refundResponse.StatusCode}");
        }

        using var refundDoc = JsonDocument.Parse(refundContent);
        var root = refundDoc.RootElement;

        // Check for error properties
        if (root.TryGetProperty("error", out var errElem))
        {
            var desc = root.TryGetProperty("error_description", out var descProp) ? descProp.GetString() : errElem.GetString();
            throw new InvalidOperationException($"PayHere API error: {desc}");
        }

        if (!root.TryGetProperty("status", out var statusProp))
        {
            throw new InvalidOperationException("PayHere refund response missing 'status' field.");
        }

        int status = statusProp.GetInt32();
        if (status != 1)
        {
            string message = root.TryGetProperty("msg", out var msg) ? msg.GetString() ?? "Unknown refund error" : "Unknown refund error";
            throw new InvalidOperationException($"PayHere rejected the refund: {message}");
        }

        // Extract refund reference number from data
        string refundNumber = string.Empty;
        if (root.TryGetProperty("data", out var dataElem))
        {
            if (dataElem.ValueKind == JsonValueKind.Number)
            {
                refundNumber = dataElem.GetRawText();
            }
            else if (dataElem.ValueKind == JsonValueKind.String)
            {
                refundNumber = dataElem.GetString() ?? string.Empty;
            }
            else if (dataElem.ValueKind == JsonValueKind.Object && dataElem.TryGetProperty("refund_id", out var refundIdElem))
            {
                refundNumber = refundIdElem.GetString() ?? refundIdElem.GetRawText();
            }
        }

        if (string.IsNullOrWhiteSpace(refundNumber))
        {
            refundNumber = $"PH-REF-{DateTime.UtcNow:yyyyMMddHHmmss}";
        }

        // Step 5: Verification via Retrieval API to confirm final payment status
        string verifiedStatus = "REFUND REQUESTED";
        try
        {
            var verifyRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            verifyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var verifyResponse = await client.SendAsync(verifyRequest);
            if (verifyResponse.IsSuccessStatusCode)
            {
                var verifyContent = await verifyResponse.Content.ReadAsStringAsync();
                using var verifyDoc = JsonDocument.Parse(verifyContent);
                var vRoot = verifyDoc.RootElement;
                if (vRoot.TryGetProperty("status", out var vStatus) && vStatus.GetInt32() == 1 &&
                    vRoot.TryGetProperty("data", out var vData) && vData.ValueKind == JsonValueKind.Array)
                {
                    var postRefundMatch = FindMatchingPayment(vData.EnumerateArray(), order, discoveredPaymentId);
                    if (postRefundMatch.HasValue && postRefundMatch.Value.TryGetProperty("status", out var postStElem))
                    {
                        var postStatus = postStElem.GetString()?.Trim().ToUpperInvariant();
                        if (!string.IsNullOrWhiteSpace(postStatus))
                        {
                            verifiedStatus = postStatus;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PayHere Retrieval Verification] Non-critical error checking final payment status post-refund.");
        }

        return new PayHereRefundExecutionResult
        {
            RefundReference = refundNumber,
            PayHereStatus = verifiedStatus,
            DiscoveredPaymentId = discoveredPaymentId
        };
    }

    /// <summary>
    /// Consumes PayHere Retrieval API (/merchant/v1/payment/search?order_id=...) to fetch live payment details.
    /// </summary>
    public async Task<PayHereRetrievalDetailsDto?> RetrievePayHerePaymentAsync(Guid orderId, Guid? currentUserId = null, bool isAdmin = false)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == orderId);
        if (order == null) return null;

        // Verify transaction ownership for seller caller (Fix D)
        if (!isAdmin && currentUserId.HasValue)
        {
            if (!order.Items.Any(i => i.Product != null && i.Product.SellerId == currentUserId.Value))
            {
                throw new UnauthorizedAccessException("You are not authorized to view payment details for this order.");
            }
        }

        var isSandboxStr = _configuration.ResolveEnv("PayHere:IsSandbox", "PAYHERE_SANDBOX", "PayHere__IsSandbox") ?? "true";
        var isSandbox = isSandboxStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        var appId = _configuration.ResolveEnv("PayHere:AppId", "PAYHERE_APP_ID", "PayHere__AppId");
        var appSecret = _configuration.ResolveEnv("PayHere:AppSecret", "PAYHERE_APP_SECRET", "PayHere__AppSecret");
        var baseUrl = isSandbox ? "https://sandbox.payhere.lk" : "https://www.payhere.lk";

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
        {
            return new PayHereRetrievalDetailsDto
            {
                Found = false,
                OrderId = order.PayHereOrderId,
                Message = "PAYHERE_APP_ID or PAYHERE_APP_SECRET is not configured on the server."
            };
        }

        try
        {
            var client = _httpClientFactory != null ? _httpClientFactory.CreateClient() : new HttpClient();
            var accessToken = await GetPayHereAccessTokenAsync(client, baseUrl, appId, appSecret);

            var searchUrl = $"{baseUrl}/merchant/v1/payment/search?order_id={Uri.EscapeDataString(order.PayHereOrderId)}";
            var searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var searchResponse = await client.SendAsync(searchRequest);
            var searchContent = await searchResponse.Content.ReadAsStringAsync();

            if (searchResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                searchContent.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
                searchContent.Contains("token expired", StringComparison.OrdinalIgnoreCase))
            {
                accessToken = await GetPayHereAccessTokenAsync(client, baseUrl, appId, appSecret, forceRefresh: true);
                searchRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                searchRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                searchResponse = await client.SendAsync(searchRequest);
                searchContent = await searchResponse.Content.ReadAsStringAsync();
            }

            if (!searchResponse.IsSuccessStatusCode)
            {
                return new PayHereRetrievalDetailsDto
                {
                    Found = false,
                    OrderId = order.PayHereOrderId,
                    Message = $"PayHere API returned HTTP {searchResponse.StatusCode}: {searchContent}"
                };
            }

            using var doc = JsonDocument.Parse(searchContent);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var stProp) ? stProp.GetInt32() : -1;
            var msg = root.TryGetProperty("msg", out var mProp) ? mProp.GetString() : "";

            if (status != 1 || !root.TryGetProperty("data", out var dataArr) ||
                dataArr.ValueKind != JsonValueKind.Array || dataArr.GetArrayLength() == 0)
            {
                return new PayHereRetrievalDetailsDto
                {
                    Found = false,
                    OrderId = order.PayHereOrderId,
                    Message = msg ?? "No payments found for this order on PayHere."
                };
            }

            // Issue 5: Match the correct transaction instead of data[0]
            var matched = FindMatchingPayment(dataArr.EnumerateArray(), order, order.PaymentId);
            if (matched == null)
            {
                return new PayHereRetrievalDetailsDto
                {
                    Found = false,
                    OrderId = order.PayHereOrderId,
                    Message = $"PayHere returned {dataArr.GetArrayLength()} transaction(s), but none matched order reference '{order.PayHereOrderId}'."
                };
            }

            var p = matched.Value;
            string? pid = null;
            if (p.TryGetProperty("payment_id", out var pidElem))
            {
                pid = pidElem.ValueKind == JsonValueKind.Number
                    ? pidElem.GetInt64().ToString()
                    : pidElem.GetString()?.Trim();
            }

            var result = new PayHereRetrievalDetailsDto
            {
                Found = true,
                PaymentId = pid,
                OrderId = p.TryGetProperty("order_id", out var oidElem) ? oidElem.GetString() : order.PayHereOrderId,
                Status = p.TryGetProperty("status", out var statElem) ? statElem.GetString() : null,
                Currency = p.TryGetProperty("currency", out var curElem) ? curElem.GetString() : null,
                Date = p.TryGetProperty("date", out var dateElem) ? dateElem.GetString() : null,
                Description = p.TryGetProperty("description", out var descElem) ? descElem.GetString() : null,
                Message = msg
            };

            if (p.TryGetProperty("amount", out var amtElem))
            {
                if (amtElem.ValueKind == JsonValueKind.Number && amtElem.TryGetDecimal(out var amt))
                    result.Amount = amt;
                else if (decimal.TryParse(amtElem.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAmt))
                    result.Amount = parsedAmt;
            }

            if (p.TryGetProperty("payment_method", out var methElem))
            {
                result.Method = methElem.TryGetProperty("method", out var mElem) ? mElem.GetString() : null;
                result.CardCustomerName = methElem.TryGetProperty("card_customer_name", out var cnElem) ? cnElem.GetString() : null;
                result.CardNumber = methElem.TryGetProperty("card_no", out var cnoElem) ? cnoElem.GetString() : null;
            }

            // If order has no payment_id stored, update it
            if (!string.IsNullOrWhiteSpace(pid) && string.IsNullOrWhiteSpace(order.PaymentId))
            {
                order.PaymentId = pid;
            }

            // Step 5 & Fix B: If refund was pending/processing and Retrieval API confirms REFUNDED, finalize the database state!
            var statusStr = result.Status?.Trim().ToUpperInvariant();
            if (string.Equals(statusStr, "REFUNDED", StringComparison.OrdinalIgnoreCase) &&
                (order.RefundStatus.Equals("RefundPending", StringComparison.OrdinalIgnoreCase) ||
                 order.RefundStatus.Equals("RefundProcessing", StringComparison.OrdinalIgnoreCase) ||
                 order.RefundStatus.Equals("Requested", StringComparison.OrdinalIgnoreCase)))
            {
                var isPartial = order.RefundReason != null && order.RefundReason.Contains("Partial Refund", StringComparison.OrdinalIgnoreCase);
                if (isPartial)
                {
                    order.RefundStatus = "PartiallyRefunded";
                    order.PaymentStatus = "PartiallyRefunded";
                }
                else
                {
                    order.RefundStatus = "Refunded";
                    order.PaymentStatus = "Refunded";
                    if (!order.OrderStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        order.OrderStatus = "Cancelled";
                        foreach (var item in order.Items)
                        {
                            if (item.Product != null)
                            {
                                item.Product.StockQuantity += item.Quantity;
                                item.Product.UpdatedAt = DateTime.UtcNow;
                            }
                        }
                    }
                }
                order.RefundProcessedAt = DateTime.UtcNow;
                order.UpdatedAt = DateTime.UtcNow;

                _logger.LogInformation("Order '{OrderId}' confirmed as REFUNDED via PayHere Retrieval API.", order.Id);
                await SendOrderRefundNotificationsAsync(order, result.Amount ?? order.TotalAmount, order.RefundReason ?? "PayHere confirmed refund", pid);
            }

            await _context.SaveChangesAsync();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PayHere Retrieval API] Error retrieving payment for order '{OrderId}'", orderId);
            return new PayHereRetrievalDetailsDto
            {
                Found = false,
                OrderId = order.PayHereOrderId,
                Message = $"Error retrieving payment: {ex.Message}"
            };
        }
    }

    private static string GenerateCarrierTrackingUrl(string? carrier, string? trackingNumber)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber)) return string.Empty;
        var c = carrier?.Trim().ToLowerInvariant() ?? "";
        var cleanTracking = trackingNumber.Trim();
        if (c.Contains("dhl"))
            return $"https://www.dhl.com/en/express/tracking.html?AWB={Uri.EscapeDataString(cleanTracking)}";
        if (c.Contains("fedex"))
            return $"https://www.fedex.com/fedextrack/?trknbr={Uri.EscapeDataString(cleanTracking)}";
        if (c.Contains("ups"))
            return $"https://www.ups.com/track?tracknum={Uri.EscapeDataString(cleanTracking)}";
        if (c.Contains("sri lanka post") || c.Contains("sl post") || c.Contains("post"))
            return $"https://slpost.gov.lk/track-trace/?track_id={Uri.EscapeDataString(cleanTracking)}";
        if (c.Contains("usps"))
            return $"https://tools.usps.com/go/TrackConfirmAction?tLabels={Uri.EscapeDataString(cleanTracking)}";
        return $"https://t.17track.net/en#nums={Uri.EscapeDataString(cleanTracking)}";
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
            PaymentId = order.PaymentId,
            TrackingNumber = order.TrackingNumber,
            ShippingCarrier = order.ShippingCarrier,
            TrackingUrl = order.TrackingUrl,
            ShippingMethod = order.ShippingMethod,
            ShippingCost = order.ShippingCost,
            ShippedAt = order.ShippedAt,
            EstimatedDeliveryDate = order.EstimatedDeliveryDate,
            RefundStatus = order.RefundStatus ?? "None",
            RefundReason = order.RefundReason,
            RefundRequestedAt = order.RefundRequestedAt,
            RefundProcessedAt = order.RefundProcessedAt,
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

        var merchantId = _configuration.ResolveEnv("PayHere:MerchantId", "PAYHERE_MERCHANT_ID", "PayHere__MerchantId");
        var merchantSecret = _configuration.ResolveEnv("PayHere:MerchantSecret", "PAYHERE_MERCHANT_SECRET", "PayHere__MerchantSecret");

        if (string.IsNullOrWhiteSpace(merchantId) || string.IsNullOrWhiteSpace(merchantSecret))
        {
            _logger.LogError("PayHere IPN verification failed: PAYHERE_MERCHANT_ID or PAYHERE_MERCHANT_SECRET is not configured in environment variables.");
            return false;
        }

        // 1. Mandatory verification checks according to PayHere security guidelines
        if (string.IsNullOrWhiteSpace(notification.md5sig) || string.IsNullOrWhiteSpace(notification.payhere_amount))
        {
            _logger.LogWarning("PayHere IPN rejected: missing md5sig or payhere_amount for order '{OrderId}'.", notification.order_id);
            return false;
        }

        // Reject missing or unexpected merchant IDs
        if (string.IsNullOrWhiteSpace(notification.merchant_id) || 
            !string.Equals(notification.merchant_id.Trim(), merchantId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("PayHere IPN Merchant ID missing or mismatch for order '{OrderId}'. Expected: {Expected}, Received: {Received}.",
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

        // 3. Handle Successful Payment (status_code == 2)
        if (notification.status_code == 2)
        {
            // Amount parsing and validation
            if (!decimal.TryParse(
                notification.payhere_amount,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var paidAmount))
            {
                _logger.LogWarning("PayHere IPN rejected: invalid payhere_amount format for order '{OrderId}'.", notification.order_id);
                return false;
            }

            if (Math.Abs(paidAmount - order.TotalAmount) > 0.01m)
            {
                _logger.LogWarning("PayHere IPN payment amount mismatch for order '{OrderId}'. Expected: {Expected}, Received: {Received}.",
                    order.Id, order.TotalAmount, paidAmount);
                return false;
            }

            // Currency validation
            var expectedCurrency = _configuration["PayHere:Currency"] ?? "USD";
            if (!string.Equals(
                notification.payhere_currency,
                expectedCurrency,
                StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("PayHere IPN payment currency mismatch for order '{OrderId}'. Expected: {Expected}, Received: {Received}.",
                    order.Id, expectedCurrency, notification.payhere_currency);
                return false;
            }

            // Payment ID is mandatory for successful checkout
            if (string.IsNullOrWhiteSpace(notification.payment_id))
            {
                _logger.LogWarning("PayHere IPN rejected: missing payment_id for successful payment on order '{OrderId}'.", notification.order_id);
                return false;
            }

            var incomingPaymentId = notification.payment_id.Trim();

            // Enforce expected payment ID if one was already recorded
            if (!string.IsNullOrWhiteSpace(order.PaymentId) &&
                !string.Equals(order.PaymentId, incomingPaymentId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("PayHere IPN rejected: payment_id mismatch for order '{OrderId}'. Stored: {Stored}, Received: {Received}.",
                    order.Id, order.PaymentId, incomingPaymentId);
                return false;
            }

            // Ensure payment ID is not associated with another order
            var isDuplicatePayment = await _context.Orders
                .AnyAsync(o => o.Id != order.Id && o.PaymentId == incomingPaymentId);
            if (isDuplicatePayment)
            {
                _logger.LogWarning("PayHere IPN rejected: payment_id '{PaymentId}' is already associated with another order.", incomingPaymentId);
                return false;
            }

            var alreadyPaid = string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase);

            order.PaymentStatus = "Paid";
            order.OrderStatus = "Processing";
            order.PaymentId = incomingPaymentId;
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order '{OrderId}' successfully marked Paid via PayHere IPN notification. PaymentId: {PaymentId}.",
                order.Id, incomingPaymentId);

            if (!alreadyPaid)
            {
                await SendPaidOrderSellerNotificationsAsync(order);
            }

            return true;
        }

        // 4. Out-of-order notification protection:
        // Do not allow delayed callbacks (e.g. pending, cancelled, or failed) to overwrite completed/paid orders.
        if (string.Equals(order.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(order.PaymentStatus, "Refunded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(order.PaymentStatus, "PartiallyRefunded", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Ignoring delayed/out-of-order PayHere IPN notification (Status code: {StatusCode}) for already completed order '{OrderId}' (Current PaymentStatus: {PaymentStatus}).",
                notification.status_code, order.Id, order.PaymentStatus);
            return true;
        }

        // 5. Handle Cancelled (-1), Failed (-2), or Chargedback (-3) callbacks
        if (notification.status_code == -1 || notification.status_code == -2 || notification.status_code == -3)
        {
            // Restore inventory for reserved stock if order wasn't previously cancelled
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
                _logger.LogInformation("Restored catalog inventory for order '{OrderId}' due to payment status code {StatusCode}.",
                    order.Id, notification.status_code);
            }

            order.OrderStatus = "Cancelled";
            order.PaymentStatus = notification.status_code switch
            {
                -1 => "Cancelled",
                -2 => "Failed",
                -3 => "Chargedback",
                _ => "Cancelled"
            };
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogWarning("Order '{OrderId}' payment marked as {PaymentStatus} (Status code: {StatusCode}). Message: {Message}.",
                order.Id, order.PaymentStatus, notification.status_code, notification.status_message);
            return true;
        }

        // 6. Handle Pending Callback (status_code == 0)
        if (notification.status_code == 0)
        {
            order.PaymentStatus = "Pending";
            if (!string.IsNullOrWhiteSpace(notification.payment_id))
            {
                order.PaymentId = notification.payment_id.Trim();
            }
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order '{OrderId}' payment is Pending in PayHere. PaymentId: {PaymentId}.",
                order.Id, notification.payment_id);
            return true;
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
        if (alreadyPaid)
        {
            return MapToDto(order);
        }

        // Verify live payment directly with PayHere via Retrieval API before marking Paid
        var retrieval = await RetrievePayHerePaymentAsync(orderId, null, isAdmin: true);
        if (retrieval != null && retrieval.Found &&
            (string.Equals(retrieval.Status, "RECEIVED", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(retrieval.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(retrieval.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(retrieval.Status, "PAID", StringComparison.OrdinalIgnoreCase)))
        {
            var verifiedPaymentId = !string.IsNullOrWhiteSpace(retrieval.PaymentId)
                ? retrieval.PaymentId.Trim()
                : paymentId?.Trim();

            order.PaymentStatus = "Paid";
            order.OrderStatus = "Processing";
            if (!string.IsNullOrWhiteSpace(verifiedPaymentId))
            {
                order.PaymentId = verifiedPaymentId;
            }
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Order '{OrderId}' payment verified as PAID via PayHere gateway. PaymentId: {PaymentId}.",
                order.Id, order.PaymentId);

            await SendPaidOrderSellerNotificationsAsync(order);
            return MapToDto(order);
        }

        // If PayHere has not confirmed payment, return current pending order without falsely marking Paid
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
                            &copy; {DateTime.UtcNow.Year} Arboveya. All rights reserved.
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

                var emailSubject = $"[Arboveya] Order Delivered Successfully — Order #{order.PayHereOrderId}";
                var emailHtml = $@"
                    <div style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; background: #ffffff; border: 1px solid #dbe6dc; border-radius: 12px; overflow: hidden;"">
                        <div style=""background: #1c3f24; padding: 24px; text-align: center;"">
                            <h1 style=""color: #ffffff; margin: 0; font-size: 24px; letter-spacing: 2px;"">ARBOVEYA</h1>
                            <p style=""color: #d6e8d8; margin: 4px 0 0 0; font-size: 12px; text-transform: uppercase; letter-spacing: 1px;"">Delivery Confirmation</p>
                        </div>
                        <div style=""padding: 24px 28px;"">
                            <h2 style=""color: #1c3f24; margin: 0 0 12px 0; font-size: 18px;"">Customer Delivery Confirmed!</h2>
                            <p style=""color: #4b5563; font-size: 14px; line-height: 1.5; margin: 0 0 18px 0;"">
                                Hello <strong>{System.Net.WebUtility.HtmlEncode(seller.FirstName)}</strong>,<br/>
                                The botanical package for order <strong>#{order.PayHereOrderId}</strong> has been successfully delivered to the customer.
                            </p>

                            <div style=""background: #edf5ee; border: 1px solid #c2dec5; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #1c3f24; line-height: 1.6;"">
                                <div><strong>Order Reference:</strong> <span style=""font-family: monospace;"">{order.PayHereOrderId}</span></div>
                                <div><strong>Delivery Status:</strong> <span style=""font-weight: bold; color: #15803d;"">Delivered &amp; Completed</span></div>
                                <div><strong>Payment Settlement:</strong> Direct Deposit (Received directly in your PayHere account)</div>
                                <div><strong>Order Amount:</strong> <span style=""font-weight: bold; font-size: 15px;"">${sellerSubtotal:F2}</span></div>
                            </div>

                            <p style=""font-size: 13px; color: #4b5563; line-height: 1.5;"">
                                Customer payment was deposited directly into your PayHere account at checkout. No holding or escrow disbursement process is required.
                            </p>
                        </div>
                        <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                            &copy; {DateTime.UtcNow.Year} Arboveya. All rights reserved.
                        </div>
                    </div>";

                await _emailService.SendEmailAsync(seller.Email, emailSubject, emailHtml);
                _logger.LogInformation("Sent delivery notification email for order '{OrderId}' to seller '{SellerEmail}'.",
                    order.Id, seller.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send seller payout clearance email for order '{OrderId}'.", order.Id);
        }
    }

    private async Task SendOrderRefundNotificationsAsync(Order order, decimal refundAmount, string reason, string? payHereRefundId = null)
    {
        try
        {
            // 1. Email to Buyer
            if (!string.IsNullOrWhiteSpace(order.CustomerEmail))
            {
                var buyerSubject = $"[Arboveya] Refund Confirmation — Order #{order.PayHereOrderId}";
                var payHereRefSnippet = !string.IsNullOrWhiteSpace(payHereRefundId)
                    ? $@"<div><strong>PayHere Refund Ref:</strong> <span style=""font-family: monospace; color: #0053a0; font-weight: bold;"">{System.Net.WebUtility.HtmlEncode(payHereRefundId)}</span></div>"
                    : "";

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
                                Your seller has processed a refund directly to your original payment method via PayHere for your Arboveya order.
                            </p>

                            <div style=""background: #f7faf7; border: 1px solid #e2eae2; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #374151; line-height: 1.6;"">
                                <div><strong>Order Reference:</strong> <span style=""font-family: monospace; color: #1c3f24;"">{order.PayHereOrderId}</span></div>
                                <div><strong>Refund Amount:</strong> <strong style=""color: #15803d; font-size: 15px;"">${refundAmount:F2}</strong></div>
                                {payHereRefSnippet}
                                <div><strong>Details:</strong> {System.Net.WebUtility.HtmlEncode(reason)}</div>
                                <div><strong>Payment Status:</strong> <span style=""color: #b91c1c; font-weight: bold;"">Refunded</span></div>
                            </div>

                            <p style=""font-size: 13px; color: #4b5563; line-height: 1.5;"">
                                Funds will return to your original payment card or PayHere wallet. Depending on your bank's card processing cycle, it usually takes <strong>5 to 10 business days</strong> for the credit to appear on your statement.
                            </p>
                        </div>
                        <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                            &copy; {DateTime.UtcNow.Year} Arboveya. All rights reserved.
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
                var sellerSubject = $"[Arboveya] Order Refund Confirmation — Order #{order.PayHereOrderId}";
                var sellerHtml = $@"
                    <div style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; background: #7f1d1d; border-radius: 12px; overflow: hidden;"">
                        <div style=""padding: 24px; text-align: center;"">
                            <h1 style=""color: #ffffff; margin: 0; font-size: 24px; letter-spacing: 2px;"">ARBOVEYA</h1>
                            <p style=""color: #fecaca; margin: 4px 0 0 0; font-size: 12px; text-transform: uppercase; letter-spacing: 1px;"">Order Refund Notice</p>
                        </div>
                        <div style=""background: #ffffff; padding: 24px 28px;"">
                            <h2 style=""color: #991b1b; margin: 0 0 12px 0; font-size: 18px;"">Order Refund Confirmed</h2>
                            <p style=""color: #4b5563; font-size: 14px; line-height: 1.5; margin: 0 0 18px 0;"">
                                Hello <strong>{System.Net.WebUtility.HtmlEncode(seller.FirstName)}</strong>,<br/>
                                Order <strong>#{order.PayHereOrderId}</strong> has been marked as refunded in your Seller Studio.
                            </p>

                            <div style=""background: #fff5f5; border: 1px solid #fed7d7; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #7f1d1d; line-height: 1.6;"">
                                <div><strong>Order Reference:</strong> <span style=""font-family: monospace;"">{order.PayHereOrderId}</span></div>
                                <div><strong>Refund Details:</strong> {System.Net.WebUtility.HtmlEncode(reason)}</div>
                                <div><strong>Inventory:</strong> Item stock has been automatically restored to your store catalog.</div>
                                <div><strong>Method:</strong> PayHere merchant manual refund.</div>
                            </div>
                        </div>
                        <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                            &copy; {DateTime.UtcNow.Year} Arboveya. All rights reserved.
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

    private async Task SendRefundRequestSellerNotificationAsync(Order order, string reason)
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
                var sellerSubject = $"[Arboveya Action Required] Refund Requested for Order #{order.PayHereOrderId}";
                var sellerHtml = $@"
                    <div style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; background: #92400e; border-radius: 12px; overflow: hidden;"">
                        <div style=""padding: 24px; text-align: center;"">
                            <h1 style=""color: #ffffff; margin: 0; font-size: 24px; letter-spacing: 2px;"">ARBOVEYA</h1>
                            <p style=""color: #fef3c7; margin: 4px 0 0 0; font-size: 12px; text-transform: uppercase; letter-spacing: 1px;"">Refund Request Notification</p>
                        </div>
                        <div style=""background: #ffffff; padding: 24px 28px;"">
                            <h2 style=""color: #92400e; margin: 0 0 12px 0; font-size: 18px;"">Customer Requested a Refund</h2>
                            <p style=""color: #4b5563; font-size: 14px; line-height: 1.5; margin: 0 0 18px 0;"">
                                Hello <strong>{System.Net.WebUtility.HtmlEncode(seller.FirstName)}</strong>,<br/>
                                Customer <strong>{System.Net.WebUtility.HtmlEncode(order.CustomerName)}</strong> has opened a refund request for order <strong>#{order.PayHereOrderId}</strong>.
                            </p>

                            <div style=""background: #fffbeb; border: 1px solid #fde68a; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #78350f; line-height: 1.6;"">
                                <div><strong>Order Reference:</strong> <span style=""font-family: monospace;"">{order.PayHereOrderId}</span></div>
                                <div><strong>Order Total:</strong> <strong>${order.TotalAmount:F2}</strong></div>
                                <div><strong>Customer Reason:</strong> {System.Net.WebUtility.HtmlEncode(reason)}</div>
                                <div><strong>Status:</strong> <span style=""color: #b45309; font-weight: bold;"">Refund Requested (Action Pending)</span></div>
                            </div>

                            <p style=""font-size: 13px; color: #4b5563; line-height: 1.5;"">
                                Please navigate to your <strong>Arboveya Seller Dashboard &gt; Manage Orders</strong> to review the details and process or resolve the refund.
                            </p>
                        </div>
                        <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                            &copy; {DateTime.UtcNow.Year} Arboveya. All rights reserved.
                        </div>
                    </div>";

                await _emailService.SendEmailAsync(seller.Email, sellerSubject, sellerHtml);
                _logger.LogInformation("Sent refund request notice email for order '{OrderId}' to seller '{SellerEmail}'.", order.Id, seller.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send refund request seller notifications for order '{OrderId}'.", order.Id);
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
