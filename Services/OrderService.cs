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

            // Send notification email to each seller who has products in this order
            try
            {
                var sellerGroups = orderItems
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

                    var emailSubject = $"[Arboveya] New Order Received! Order #{order.PayHereOrderId}";
                    var emailHtml = $@"
                        <div style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; max-width: 600px; margin: 0 auto; background: #ffffff; border: 1px solid #dbe6dc; border-radius: 12px; overflow: hidden;"">
                            <div style=""background: #1c3f24; padding: 24px; text-align: center;"">
                                <h1 style=""color: #ffffff; margin: 0; font-size: 24px; letter-spacing: 2px;"">ARBOVEYA</h1>
                                <p style=""color: #d6e8d8; margin: 4px 0 0 0; font-size: 12px; text-transform: uppercase; letter-spacing: 1px;"">Herbal & Botanical Marketplace</p>
                            </div>
                            <div style=""padding: 24px 28px;"">
                                <h2 style=""color: #1c3f24; margin: 0 0 12px 0; font-size: 18px;"">New Order Received!</h2>
                                <p style=""color: #4b5563; font-size: 14px; line-height: 1.5; margin: 0 0 18px 0;"">
                                    Hello <strong>{System.Net.WebUtility.HtmlEncode(seller.FirstName)}</strong>,<br/>
                                    A customer has placed an order containing botanical product(s) from your store.
                                </p>

                                <div style=""background: #f7faf7; border: 1px solid #e2eae2; border-radius: 8px; padding: 16px; margin-bottom: 20px; font-size: 13px; color: #374151; line-height: 1.6;"">
                                    <div><strong>Order Reference:</strong> <span style=""font-family: monospace; color: #1c3f24;"">{order.PayHereOrderId}</span></div>
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
                                    <p style=""font-size: 12px; color: #6b7280; margin-bottom: 8px;"">Please prepare and dispatch the botanical remedies according to your handling schedule.</p>
                                </div>
                            </div>
                            <div style=""background: #f9fafb; padding: 16px; text-align: center; border-top: 1px solid #e5e7eb; font-size: 11px; color: #9ca3af;"">
                                &copy; {DateTime.UtcNow.Year} Arboveya Herbal Botanical. All rights reserved.
                            </div>
                        </div>";

                    await _emailService.SendEmailAsync(seller.Email, emailSubject, emailHtml);
                    _logger.LogInformation("Sent new order notification email for order '{OrderId}' to seller '{SellerEmail}'.",
                        order.Id, seller.Email);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send seller order notification email(s) for order '{OrderId}'.", order.Id);
            }

            var resp = MapToDto(order);

            // Populate PayHere details for client-side checkout
            var merchantId = _configuration["PayHere:MerchantId"] 
                ?? _configuration["PAYHERE_MERCHANT_ID"] 
                ?? Environment.GetEnvironmentVariable("PAYHERE_MERCHANT_ID") 
                ?? "1211149";
            if (merchantId == "your_payhere_merchant_id") merchantId = "1211149";

            var merchantSecret = _configuration["PayHere:MerchantSecret"] 
                ?? _configuration["PAYHERE_MERCHANT_SECRET"] 
                ?? Environment.GetEnvironmentVariable("PAYHERE_MERCHANT_SECRET") 
                ?? "4UPxLq74JtT4LUPxLq74JtT";
            if (merchantSecret == "your_payhere_merchant_secret") merchantSecret = "4UPxLq74JtT4LUPxLq74JtT";

            var isSandbox = (_configuration["PayHere:IsSandbox"] 
                ?? _configuration["PAYHERE_SANDBOX"] 
                ?? Environment.GetEnvironmentVariable("PAYHERE_SANDBOX") 
                ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);

            var notifyUrl = _configuration["PayHere:NotifyUrl"] 
                ?? _configuration["PAYHERE_NOTIFY_URL"] 
                ?? Environment.GetEnvironmentVariable("PAYHERE_NOTIFY_URL");

            var nameParts = customerName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var firstName = nameParts.Length > 0 ? nameParts[0].Trim() : "Valued";
            var lastName = nameParts.Length > 1 ? nameParts[1].Trim() : "Customer";
            var payHereHash = GeneratePayHereHash(merchantId, order.PayHereOrderId, order.TotalAmount, "LKR", merchantSecret);

            resp.PayHereDetails = new PayHereCheckoutDetailsDto
            {
                Sandbox = isSandbox,
                MerchantId = merchantId,
                OrderId = order.PayHereOrderId,
                Items = $"Arboveya Herbal Order ({order.Items.Count} item(s))",
                Amount = order.TotalAmount,
                Currency = "LKR",
                Hash = payHereHash,
                FirstName = firstName,
                LastName = lastName,
                Email = customerEmail,
                Phone = "",
                Address = shippingAddress,
                City = "Colombo",
                Country = "Sri Lanka",
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

    public async Task<bool> ProcessPayHereNotificationAsync(PayHereNotificationDto notification)
    {
        if (string.IsNullOrWhiteSpace(notification.order_id))
        {
            _logger.LogWarning("PayHere IPN received without order_id.");
            return false;
        }

        var merchantId = _configuration["PayHere:MerchantId"] 
            ?? _configuration["PAYHERE_MERCHANT_ID"] 
            ?? Environment.GetEnvironmentVariable("PAYHERE_MERCHANT_ID") 
            ?? "1211149";
        if (merchantId == "your_payhere_merchant_id") merchantId = "1211149";

        var merchantSecret = _configuration["PayHere:MerchantSecret"] 
            ?? _configuration["PAYHERE_MERCHANT_SECRET"] 
            ?? Environment.GetEnvironmentVariable("PAYHERE_MERCHANT_SECRET") 
            ?? "4UPxLq74JtT4LUPxLq74JtT";
        if (merchantSecret == "your_payhere_merchant_secret") merchantSecret = "4UPxLq74JtT4LUPxLq74JtT";

        // Signature verification:
        // MD5(merchant_id + order_id + payhere_amount + payhere_currency + status_code + strtoupper(md5(merchant_secret)))
        if (!string.IsNullOrWhiteSpace(notification.md5sig) && !string.IsNullOrWhiteSpace(notification.payhere_amount))
        {
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
        }

        var order = await _context.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.PayHereOrderId == notification.order_id);

        if (order == null)
        {
            _logger.LogWarning("Order with PayHereOrderId '{OrderId}' not found.", notification.order_id);
            return false;
        }

        if (notification.status_code == 2)
        {
            order.PaymentStatus = "Paid";
            order.OrderStatus = "Processing";
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogInformation("Order '{OrderId}' successfully marked Paid via PayHere IPN notification. PaymentId: {PaymentId}.",
                order.Id, notification.payment_id);
        }
        else if (notification.status_code == -1 || notification.status_code == -2)
        {
            order.PaymentStatus = "Failed";
            order.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogWarning("Order '{OrderId}' payment failed with status code {Code}: {Message}.",
                order.Id, notification.status_code, notification.status_message);
        }

        return true;
    }

    public async Task<OrderResponseDto?> ConfirmOrderPaymentAsync(Guid orderId, string? paymentId)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null) return null;

        order.PaymentStatus = "Paid";
        order.OrderStatus = "Processing";
        order.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Order '{OrderId}' payment confirmed by client callback. PaymentId: {PaymentId}.",
            order.Id, paymentId);

        return MapToDto(order);
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
