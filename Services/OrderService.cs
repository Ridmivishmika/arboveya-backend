using System.Security.Cryptography;
using System.Text;
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
        string? paymentId = null)
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
        var effectivePaymentId = !string.IsNullOrWhiteSpace(paymentId) ? paymentId.Trim() : order.PaymentId;

        // If automated PayHere refund requested and no external refund ID provided yet
        if (autoRefundSandbox && string.IsNullOrWhiteSpace(payHereRefundId))
        {
            payHereRefundId = await ExecutePayHereSandboxRefundAsync(order, amountToRefund, reason ?? "Automated refund processed via PayHere", effectivePaymentId);
        }

        // Persist paymentId if provided/resolved so future queries track PayHere payment identifier
        if (!string.IsNullOrWhiteSpace(effectivePaymentId) && string.IsNullOrWhiteSpace(order.PaymentId))
        {
            order.PaymentId = effectivePaymentId;
        }

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
        order.RefundStatus = "Refunded";
        order.RefundProcessedAt = DateTime.UtcNow;

        var parts = new List<string>();
        if (autoRefundSandbox)
        {
            parts.Add("PayHere Auto-Refund");
        }
        else
        {
            parts.Add("Manual refund via PayHere");
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
        await _context.SaveChangesAsync();

        _logger.LogInformation("Order '{OrderId}' successfully refunded (${RefundAmount:F2}) via PayHere (Auto: {AutoSandbox}). Reason: '{Reason}'.",
            order.Id, amountToRefund, autoRefundSandbox, order.RefundReason);

        // Send refund notification emails to buyer and seller
        await SendOrderRefundNotificationsAsync(order, amountToRefund, order.RefundReason, payHereRefundId);

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
                    "Please verify your PayHere App ID and App Secret in Settings > API Keys, and ensure the 'Automated Charging API' permission is checked.");
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

    /// <summary>
    /// Executes automated PayHere Refund.
    /// Strictly adheres to the PayHere Refund API documentation:
    /// - Derives Basic Authorization code from App ID & App Secret
    /// - Retrieves / reuses OAuth Access Token from /merchant/v1/oauth/token
    /// - Consumes /merchant/v1/payment/refund with Bearer token
    /// - Passes payment_id, description, and amount (for partial refunds)
    /// - Parses status (1: success, 0: error initiating, -1: refund failed, -2: auth error)
    /// - Extracts unique refund number from data (e.g. 560034010257)
    /// </summary>
    private async Task<string> ExecutePayHereSandboxRefundAsync(Order order, decimal amount, string reason, string? paymentId)
    {
        var isSandboxStr = _configuration.ResolveEnv("PayHere:IsSandbox", "PAYHERE_SANDBOX", "PayHere__IsSandbox") ?? "true";
        var isSandbox = isSandboxStr.Equals("true", StringComparison.OrdinalIgnoreCase);
        var appId = _configuration.ResolveEnv("PayHere:AppId", "PAYHERE_APP_ID", "PayHere__AppId");
        var appSecret = _configuration.ResolveEnv("PayHere:AppSecret", "PAYHERE_APP_SECRET", "PayHere__AppSecret");
        var baseUrl = isSandbox ? "https://sandbox.payhere.lk" : "https://www.payhere.lk";

        if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(appSecret))
        {
            if (string.IsNullOrWhiteSpace(paymentId))
            {
                throw new InvalidOperationException(
                    "PayHere requires the transaction Payment ID (payment_id) to process an automated refund. " +
                    "Please locate your payment in your PayHere account under 'Payments' tab, click to view the Payment ID, " +
                    "or process the refund directly in your PayHere Merchant Portal (https://merchant.payhere.lk > Payments > Request Refund) and enter the reference using Manual Portal Entry.");
            }

            try
            {
                var client = _httpClientFactory != null ? _httpClientFactory.CreateClient() : new HttpClient();

                // 1. Retrieve or refresh OAuth Access Token
                var accessToken = await GetPayHereAccessTokenAsync(client, baseUrl, appId, appSecret);

                // 2. Build and execute Refund API call (POST /merchant/v1/payment/refund)
                var refundUrl = $"{baseUrl}/merchant/v1/payment/refund";
                var refundPayload = new Dictionary<string, object>
                {
                    { "payment_id", paymentId.Trim() },
                    { "description", string.IsNullOrWhiteSpace(reason) ? $"Refund for order {order.PayHereOrderId}" : reason.Trim() }
                };

                // As per PayHere documentation:
                // For partial refunds: include "amount": "100.50"
                // For full refunds: omit amount so PayHere processes the full amount
                if (amount > 0 && amount < order.TotalAmount)
                {
                    refundPayload["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture);
                }

                var refundRequest = new HttpRequestMessage(HttpMethod.Post, refundUrl);
                refundRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                refundRequest.Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(refundPayload),
                    Encoding.UTF8,
                    "application/json");

                var refundResponse = await client.SendAsync(refundRequest);
                var refundContent = await refundResponse.Content.ReadAsStringAsync();

                // If token expired, clear cache and retry once
                if (refundResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    refundContent.Contains("invalid_token", StringComparison.OrdinalIgnoreCase) ||
                    refundContent.Contains("token expired", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("[PayHere API] Token expired or unauthorized. Refreshing OAuth Access Token and retrying refund...");
                    _cachedAccessToken = null;
                    _tokenExpiresAt = DateTime.MinValue;

                    accessToken = await GetPayHereAccessTokenAsync(client, baseUrl, appId, appSecret, forceRefresh: true);
                    refundRequest = new HttpRequestMessage(HttpMethod.Post, refundUrl);
                    refundRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    refundRequest.Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(refundPayload),
                        Encoding.UTF8,
                        "application/json");

                    refundResponse = await client.SendAsync(refundRequest);
                    refundContent = await refundResponse.Content.ReadAsStringAsync();
                }

                _logger.LogInformation("[PayHere Refund API Response] Status: {StatusCode}, Body: {Body}", refundResponse.StatusCode, refundContent);

                if (!refundResponse.IsSuccessStatusCode)
                {
                    if (refundResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        try
                        {
                            using var unauthDoc = System.Text.Json.JsonDocument.Parse(refundContent);
                            if (unauthDoc.RootElement.TryGetProperty("msg", out var unauthMsg) && 
                                unauthMsg.GetString()?.Contains("Access denied for the domain", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                throw new InvalidOperationException(
                                    "PayHere Access Denied for Domain: For Sandbox testing, please set the allowed domain of your API Key to '*' in PayHere Dashboard (Settings > API Keys). In Live, contact support@payhere.lk to whitelist your server IP/domain.");
                            }
                            if (unauthDoc.RootElement.TryGetProperty("error_description", out var unauthDesc))
                            {
                                throw new InvalidOperationException($"PayHere Unauthorized: {unauthDesc.GetString()}");
                            }
                        }
                        catch (System.Text.Json.JsonException) { }
                    }

                    throw new InvalidOperationException($"PayHere Refund API returned HTTP {refundResponse.StatusCode}: {refundContent}");
                }

                using var refundDoc = System.Text.Json.JsonDocument.Parse(refundContent);
                var root = refundDoc.RootElement;

                // Check for OAuth error in payload
                if (root.TryGetProperty("error", out var apiErrElem))
                {
                    var desc = root.TryGetProperty("error_description", out var descProp) ? descProp.GetString() : apiErrElem.GetString();
                    throw new InvalidOperationException($"PayHere API Error: {desc}");
                }

                // Check PayHere Payment Status Codes:
                //  1 - success
                //  0 - error initiating the refund
                // -1 - refund failed. check msg for details
                // -2 - authentication error
                if (root.TryGetProperty("status", out var statusProp))
                {
                    var statusCode = statusProp.GetInt32();
                    var msg = root.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() : "PayHere rejected the refund.";

                    if (statusCode == -2)
                    {
                        throw new InvalidOperationException(
                            $"PayHere Authentication Error: {msg}. Please ensure your API Key has the 'Automated Charging API' permission enabled and that your domain whitelist is configured (use '*' for Sandbox) in PayHere Settings > API Keys.");
                    }

                    if (statusCode == -1)
                    {
                        if (msg != null && msg.Contains("Access denied for the domain", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"PayHere Access Denied for Domain: {msg}. For Sandbox testing, set the allowed domain of your API Key to '*' in PayHere Settings > API Keys. In Live, contact support@payhere.lk to whitelist your server IP.");
                        }

                        throw new InvalidOperationException(
                            $"PayHere Refund Failed: {msg}. Please verify that the Payment ID ('{paymentId}') is a valid card payment on PayHere. Note that only card payment methods (Visa, Mastercard, American Express, Discover, Diners Club) can be refunded via PayHere API. Non-card methods (Frimi, Genie, eZcash, mCash, Vishwa) cannot be refunded via PayHere and must be refunded manually via bank transfer.");
                    }

                    if (statusCode == 0)
                    {
                        throw new InvalidOperationException(
                            $"PayHere Error Initiating Refund: {msg}. PayHere recovers refund funds at day-end from your merchant bank account balance or future payouts. Please ensure sufficient funds are available in your account.");
                    }

                    if (statusCode != 1)
                    {
                        throw new InvalidOperationException($"PayHere Refund Error ({statusCode}): {msg}");
                    }
                }

                // Extract unique refund number from data (e.g. { "status": 1, "msg": "Successfully processed the refund", "data": 560034010257 })
                string refundNumber = string.Empty;
                if (root.TryGetProperty("data", out var dataElem))
                {
                    if (dataElem.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        refundNumber = dataElem.GetRawText();
                    }
                    else if (dataElem.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        refundNumber = dataElem.GetString() ?? string.Empty;
                    }
                    else if (dataElem.ValueKind == System.Text.Json.JsonValueKind.Object && dataElem.TryGetProperty("refund_id", out var refundIdElem))
                    {
                        refundNumber = refundIdElem.GetString() ?? refundIdElem.GetRawText();
                    }
                }

                if (string.IsNullOrWhiteSpace(refundNumber))
                {
                    refundNumber = $"PH-REF-{DateTime.UtcNow:yyyyMMddHHmmss}";
                }

                return refundNumber;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PayHere API] Automated refund API call encountered an unexpected error.");
                throw new InvalidOperationException($"PayHere automated refund failed: {ex.Message}. You can issue the refund directly in your PayHere Merchant Dashboard (Payments > Request Refund) and record it using Manual Portal Entry.");
            }
        }

        // If credentials are NOT configured:
        if (!isSandbox)
        {
            throw new InvalidOperationException(
                "PayHere AppId and AppSecret are not configured in the server environment. " +
                "To process refunds via PayHere, obtain your App ID and App Secret from PayHere Dashboard (Settings > API Keys) and configure PAYHERE_APP_ID and PAYHERE_APP_SECRET, or perform the refund directly in merchant.payhere.lk and use 'Manual Portal Entry'.");
        }

        // In Sandbox mode without credentials: generate simulated reference and inform via log
        var generatedRefundId = $"PH-REF-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        _logger.LogWarning("[PayHere Sandbox Simulation] PAYHERE_APP_ID and PAYHERE_APP_SECRET are empty. Simulated sandbox refund generated: '{RefundId}'",
            generatedRefundId);
        return generatedRefundId;
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
            if (!string.IsNullOrWhiteSpace(notification.payment_id))
            {
                order.PaymentId = notification.payment_id.Trim();
            }
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
            if (!string.IsNullOrWhiteSpace(notification.payment_id))
            {
                order.PaymentId = notification.payment_id.Trim();
            }
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
        if (!string.IsNullOrWhiteSpace(paymentId))
        {
            order.PaymentId = paymentId.Trim();
        }
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
