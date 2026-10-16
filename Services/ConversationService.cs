using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class ConversationService : IConversationService
{
    private readonly AppDbContext _context;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        AppDbContext context,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<ConversationService> logger)
    {
        _context = context;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ConversationDto> GetOrCreateProductConversationAsync(Guid buyerId, Guid productId, string? initialMessage = null)
    {
        var product = await _context.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId);

        if (product == null)
        {
            throw new ArgumentException($"Product with ID '{productId}' was not found.");
        }

        // Reuse existing conversation if one already exists for this buyer and product
        var existing = await _context.Conversations
            .Include(c => c.Buyer)
            .Include(c => c.Seller)
            .Include(c => c.Product)
            .FirstOrDefaultAsync(c => c.BuyerId == buyerId && c.ProductId == productId);

        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(initialMessage))
            {
                await SendMessageAsync(existing.Id, buyerId, "Customer", initialMessage.Trim());
            }
            return (await MapToDtoAsync(existing.Id, buyerId))!;
        }

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BuyerId = buyerId,
            SellerId = product.SellerId,
            ProductId = productId,
            Type = "ProductInquiry",
            Subject = $"Inquiry: {product.Name}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            BuyerLastReadAt = DateTime.UtcNow
        };

        try
        {
            _context.Conversations.Add(conversation);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // Concurrent creation fallback in case another thread inserted right before
            _logger.LogWarning(ex, "Concurrent conversation creation detected for buyer {BuyerId} and product {ProductId}", buyerId, productId);
            var reloaded = await _context.Conversations
                .FirstOrDefaultAsync(c => c.BuyerId == buyerId && c.ProductId == productId);

            if (reloaded != null)
            {
                conversation = reloaded;
            }
            else
            {
                throw;
            }
        }

        if (!string.IsNullOrWhiteSpace(initialMessage))
        {
            await SendMessageAsync(conversation.Id, buyerId, "Customer", initialMessage.Trim());
        }

        return (await MapToDtoAsync(conversation.Id, buyerId))!;
    }

    public async Task<ConversationDto> GetOrCreateOrderConversationAsync(Guid callerId, Guid orderId, string? initialMessage = null)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
                .ThenInclude(i => i.Product)
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId);

        if (order == null)
        {
            throw new ArgumentException($"Order with ID '{orderId}' was not found.");
        }

        // Security check: Must ensure order belongs to authenticated user OR caller is seller of an item in this order
        bool isBuyer = order.UserId != null && order.UserId == callerId;
        bool isSeller = order.Items.Any(i => i.Product != null && i.Product.SellerId == callerId);

        if (!isBuyer && !isSeller)
        {
            throw new UnauthorizedAccessException("You are not authorized to contact regarding this order.");
        }

        Guid actualBuyerId = order.UserId ?? callerId;
        Guid? actualSellerId = order.Items.FirstOrDefault(i => i.Product?.SellerId != null)?.Product?.SellerId;
        if (isSeller && actualSellerId == null)
        {
            actualSellerId = callerId;
        }

        // Reuse existing conversation if one already exists for this order
        var existing = await _context.Conversations
            .Include(c => c.Buyer)
            .Include(c => c.Seller)
            .Include(c => c.Order)
            .FirstOrDefaultAsync(c => c.OrderId == orderId && (c.BuyerId == actualBuyerId || c.SellerId == actualSellerId));

        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(initialMessage))
            {
                string senderRole = isSeller ? "Seller" : "Customer";
                await SendMessageAsync(existing.Id, callerId, senderRole, initialMessage.Trim());
            }
            return (await MapToDtoAsync(existing.Id, callerId))!;
        }

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            BuyerId = actualBuyerId,
            SellerId = actualSellerId,
            OrderId = orderId,
            Type = "OrderSupport",
            Subject = $"Order #{order.PayHereOrderId ?? (order.Id.ToString()[..8].ToUpper())}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            BuyerLastReadAt = isBuyer ? DateTime.UtcNow : null,
            SellerLastReadAt = isSeller ? DateTime.UtcNow : null
        };

        try
        {
            _context.Conversations.Add(conversation);
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Concurrent conversation creation detected for caller {CallerId} and order {OrderId}", callerId, orderId);
            var reloaded = await _context.Conversations
                .FirstOrDefaultAsync(c => c.OrderId == orderId);

            if (reloaded != null)
            {
                conversation = reloaded;
            }
            else
            {
                throw;
            }
        }

        if (!string.IsNullOrWhiteSpace(initialMessage))
        {
            string senderRole = isSeller ? "Seller" : "Customer";
            await SendMessageAsync(conversation.Id, callerId, senderRole, initialMessage.Trim());
        }

        return (await MapToDtoAsync(conversation.Id, callerId))!;
    }

    public async Task<List<ConversationDto>> GetBuyerConversationsAsync(Guid buyerId)
    {
        var conversationIds = await _context.Conversations
            .AsNoTracking()
            .Where(c => c.BuyerId == buyerId || c.SellerId == buyerId)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Select(c => c.Id)
            .ToListAsync();

        var list = new List<ConversationDto>();
        foreach (var id in conversationIds)
        {
            var dto = await MapToDtoAsync(id, buyerId);
            if (dto != null) list.Add(dto);
        }
        return list;
    }

    public async Task<List<ConversationDto>> GetSellerConversationsAsync(Guid sellerId)
    {
        var conversationIds = await _context.Conversations
            .AsNoTracking()
            .Where(c => c.SellerId == sellerId || c.BuyerId == sellerId)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Select(c => c.Id)
            .ToListAsync();

        var list = new List<ConversationDto>();
        foreach (var id in conversationIds)
        {
            var dto = await MapToDtoAsync(id, sellerId);
            if (dto != null) list.Add(dto);
        }
        return list;
    }

    public async Task<List<ConversationDto>> GetAllConversationsForAdminAsync(string? type = null, bool? unreadOnly = null, string? search = null)
    {
        var query = _context.Conversations
            .Include(c => c.Buyer)
            .Include(c => c.Seller)
            .Include(c => c.Product)
            .Include(c => c.Order)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(c => c.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(c =>
                c.Subject.ToLower().Contains(term) ||
                (c.Buyer != null && (c.Buyer.FirstName.ToLower().Contains(term) || c.Buyer.LastName.ToLower().Contains(term) || c.Buyer.Email.ToLower().Contains(term))) ||
                (c.Product != null && c.Product.Name.ToLower().Contains(term)) ||
                (c.Order != null && (c.Order.PayHereOrderId.ToLower().Contains(term) || c.Order.CustomerName.ToLower().Contains(term))));
        }

        var conversations = await query
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToListAsync();

        var list = new List<ConversationDto>();
        foreach (var c in conversations)
        {
            var dto = await MapToDtoAsync(c.Id, Guid.Empty, isAdmin: true);
            if (dto != null)
            {
                if (unreadOnly == true && dto.UnreadCount == 0)
                {
                    continue;
                }
                list.Add(dto);
            }
        }
        return list;
    }

    public async Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId, Guid currentUserId, string currentUserRole)
    {
        var conversation = await _context.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conversation == null) return null;

        bool isAdmin = string.Equals(currentUserRole, "Admin", StringComparison.OrdinalIgnoreCase);

        // Security check: Only Admin, the Buyer, or the assigned Seller may access this conversation
        if (!isAdmin)
        {
            bool isBuyer = conversation.BuyerId == currentUserId;
            bool isSeller = conversation.SellerId != null && conversation.SellerId == currentUserId;

            if (!isBuyer && !isSeller)
            {
                throw new UnauthorizedAccessException("You are not authorized to view this conversation.");
            }
        }

        return await MapToDtoAsync(conversationId, currentUserId, isAdmin);
    }

    public async Task<List<MessageDto>> GetMessagesAsync(Guid conversationId, Guid currentUserId, string currentUserRole)
    {
        var conversation = await _context.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conversation == null)
        {
            throw new KeyNotFoundException($"Conversation with ID '{conversationId}' was not found.");
        }

        bool isAdmin = string.Equals(currentUserRole, "Admin", StringComparison.OrdinalIgnoreCase);

        if (!isAdmin)
        {
            bool isBuyer = conversation.BuyerId == currentUserId;
            bool isSeller = conversation.SellerId != null && conversation.SellerId == currentUserId;

            if (!isBuyer && !isSeller)
            {
                throw new UnauthorizedAccessException("You are not authorized to access messages in this conversation.");
            }
        }

        var messages = await _context.Messages
            .Include(m => m.Sender)
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MessageDto
            {
                Id = m.Id,
                ConversationId = m.ConversationId,
                SenderId = m.SenderId,
                SenderName = m.Sender != null ? $"{m.Sender.FirstName} {m.Sender.LastName}".Trim() : "User",
                SenderRole = m.Sender != null ? m.Sender.Role : "Customer",
                MessageText = m.MessageText,
                CreatedAt = m.CreatedAt,
                ReadAt = m.ReadAt,
                IsFromBuyer = m.SenderId == conversation.BuyerId,
                IsMine = m.SenderId == currentUserId
            })
            .ToListAsync();

        return messages;
    }

    public async Task<MessageDto> SendMessageAsync(Guid conversationId, Guid senderId, string senderRole, string messageText)
    {
        if (string.IsNullOrWhiteSpace(messageText))
        {
            throw new ArgumentException("Message text cannot be empty.");
        }

        if (messageText.Length > 4000)
        {
            throw new ArgumentException("Message text cannot exceed 4000 characters.");
        }

        var conversation = await _context.Conversations
            .Include(c => c.Buyer)
            .Include(c => c.Seller)
            .FirstOrDefaultAsync(c => c.Id == conversationId);

        if (conversation == null)
        {
            throw new KeyNotFoundException($"Conversation with ID '{conversationId}' was not found.");
        }

        bool isAdmin = string.Equals(senderRole, "Admin", StringComparison.OrdinalIgnoreCase);
        bool isBuyer = conversation.BuyerId == senderId;
        bool isSeller = conversation.SellerId != null && conversation.SellerId == senderId;

        // If conversation had no specific seller assigned and admin/seller replies, assign seller if appropriate
        if (!isBuyer && !isSeller && !isAdmin)
        {
            throw new UnauthorizedAccessException("You are not authorized to send messages in this conversation.");
        }

        var sender = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == senderId);
        if (sender == null)
        {
            throw new ArgumentException("Invalid sender identity.");
        }

        var now = DateTime.UtcNow;

        var message = new Message
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderId = senderId,
            MessageText = messageText.Trim(),
            CreatedAt = now
        };

        _context.Messages.Add(message);

        // Update conversation activity
        conversation.LastMessageAt = now;
        conversation.UpdatedAt = now;

        if (isBuyer)
        {
            conversation.BuyerLastReadAt = now;
        }
        else
        {
            conversation.SellerLastReadAt = now;
        }

        await _context.SaveChangesAsync();

        // Optional email notification (fire-and-forget / non-blocking)
        _ = Task.Run(async () =>
        {
            try
            {
                if (isBuyer)
                {
                    // Buyer sent message -> notify seller or admin
                    string recipientEmail = conversation.Seller?.Email ?? _configuration["Admin:Email"] ?? "arboveya@gmail.com";
                    string senderDisplayName = $"{sender.FirstName} {sender.LastName}".Trim();
                    string body = $@"
                        <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #e0e0e0; border-radius: 8px;'>
                            <h2 style='color: #24492d;'>New Customer Message on Arboveya</h2>
                            <p><strong>Customer:</strong> {senderDisplayName} ({sender.Email})</p>
                            <p><strong>Subject:</strong> {conversation.Subject}</p>
                            <hr style='border: none; border-top: 1px solid #eee; margin: 15px 0;' />
                            <blockquote style='background-color: #f7f9f7; padding: 12px; border-left: 4px solid #24492d; margin: 0;'>
                                {System.Net.WebUtility.HtmlEncode(message.MessageText)}
                            </blockquote>
                            <p style='margin-top: 20px;'>Please log in to your Arboveya dashboard to view and reply to this conversation.</p>
                        </div>";

                    await _emailService.SendEmailAsync(recipientEmail, $"[Arboveya Messages] {conversation.Subject}", body, sender.Email);
                }
                else if (conversation.Buyer != null)
                {
                    // Seller/Admin replied -> notify buyer
                    string body = $@"
                        <div style='font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; border: 1px solid #e0e0e0; border-radius: 8px;'>
                            <h2 style='color: #24492d;'>Reply to Your Inquiry on Arboveya</h2>
                            <p><strong>Regarding:</strong> {conversation.Subject}</p>
                            <hr style='border: none; border-top: 1px solid #eee; margin: 15px 0;' />
                            <blockquote style='background-color: #f7f9f7; padding: 12px; border-left: 4px solid #24492d; margin: 0;'>
                                {System.Net.WebUtility.HtmlEncode(message.MessageText)}
                            </blockquote>
                            <p style='margin-top: 20px;'>You can view your full message history in your Arboveya account.</p>
                        </div>";

                    await _emailService.SendEmailAsync(conversation.Buyer.Email, $"[Arboveya] Reply to: {conversation.Subject}", body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send background email notification for message in conversation {ConversationId}", conversationId);
            }
        });

        return new MessageDto
        {
            Id = message.Id,
            ConversationId = message.ConversationId,
            SenderId = message.SenderId,
            SenderName = $"{sender.FirstName} {sender.LastName}".Trim(),
            SenderRole = sender.Role,
            MessageText = message.MessageText,
            CreatedAt = message.CreatedAt,
            ReadAt = message.ReadAt,
            IsFromBuyer = isBuyer,
            IsMine = true
        };
    }

    public async Task<bool> MarkConversationAsReadAsync(Guid conversationId, Guid currentUserId, string currentUserRole)
    {
        var conversation = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation == null) return false;

        bool isAdmin = string.Equals(currentUserRole, "Admin", StringComparison.OrdinalIgnoreCase);
        bool isBuyer = conversation.BuyerId == currentUserId;
        bool isSeller = conversation.SellerId != null && conversation.SellerId == currentUserId;

        if (!isAdmin && !isBuyer && !isSeller)
        {
            return false;
        }

        var now = DateTime.UtcNow;

        if (isBuyer)
        {
            conversation.BuyerLastReadAt = now;
            var unreadMessages = await _context.Messages
                .Where(m => m.ConversationId == conversationId && m.SenderId != currentUserId && m.ReadAt == null)
                .ToListAsync();

            foreach (var msg in unreadMessages)
            {
                msg.ReadAt = now;
            }
        }
        else
        {
            // Seller or Admin marking as read
            conversation.SellerLastReadAt = now;
            var unreadMessages = await _context.Messages
                .Where(m => m.ConversationId == conversationId && m.SenderId == conversation.BuyerId && m.ReadAt == null)
                .ToListAsync();

            foreach (var msg in unreadMessages)
            {
                msg.ReadAt = now;
            }
        }

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<int> GetUnreadCountAsync(Guid currentUserId, string currentUserRole)
    {
        bool isAdmin = string.Equals(currentUserRole, "Admin", StringComparison.OrdinalIgnoreCase);

        if (isAdmin)
        {
            // Admin sees unread count of all buyer inquiries that haven't been read by seller/admin
            return await _context.Conversations
                .CountAsync(c => c.LastMessageAt != null &&
                                 c.LastMessageAt > (c.SellerLastReadAt ?? DateTime.MinValue) &&
                                 _context.Messages
                                     .Where(m => m.ConversationId == c.Id)
                                     .OrderByDescending(m => m.CreatedAt)
                                     .Select(m => m.SenderId)
                                     .FirstOrDefault() == c.BuyerId);
        }

        bool isSeller = string.Equals(currentUserRole, "Seller", StringComparison.OrdinalIgnoreCase);
        if (isSeller)
        {
            return await _context.Conversations
                .Where(c => c.SellerId == currentUserId)
                .CountAsync(c => c.LastMessageAt != null &&
                                 c.LastMessageAt > (c.SellerLastReadAt ?? DateTime.MinValue) &&
                                 _context.Messages
                                     .Where(m => m.ConversationId == c.Id)
                                     .OrderByDescending(m => m.CreatedAt)
                                     .Select(m => m.SenderId)
                                     .FirstOrDefault() == c.BuyerId);
        }

        // Customer / Buyer
        return await _context.Conversations
            .Where(c => c.BuyerId == currentUserId)
            .CountAsync(c => c.LastMessageAt != null &&
                             c.LastMessageAt > (c.BuyerLastReadAt ?? DateTime.MinValue) &&
                             _context.Messages
                                 .Where(m => m.ConversationId == c.Id)
                                 .OrderByDescending(m => m.CreatedAt)
                                 .Select(m => m.SenderId)
                                 .FirstOrDefault() != currentUserId);
    }

    private async Task<ConversationDto?> MapToDtoAsync(Guid conversationId, Guid currentUserId, bool isAdmin = false)
    {
        var c = await _context.Conversations
            .Include(x => x.Buyer)
            .Include(x => x.Seller)
            .Include(x => x.Product)
            .Include(x => x.Order)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == conversationId);

        if (c == null) return null;

        var lastMessage = await _context.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == c.Id)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        int unreadCount = 0;
        if (isAdmin || (c.SellerId != null && c.SellerId == currentUserId))
        {
            // Unread messages from buyer for admin / seller
            unreadCount = await _context.Messages
                .CountAsync(m => m.ConversationId == c.Id && m.SenderId == c.BuyerId && (c.SellerLastReadAt == null || m.CreatedAt > c.SellerLastReadAt));
        }
        else
        {
            // Unread messages from seller/admin for buyer
            unreadCount = await _context.Messages
                .CountAsync(m => m.ConversationId == c.Id && m.SenderId != c.BuyerId && (c.BuyerLastReadAt == null || m.CreatedAt > c.BuyerLastReadAt));
        }

        var dto = new ConversationDto
        {
            Id = c.Id,
            BuyerId = c.BuyerId,
            BuyerName = c.Buyer != null ? $"{c.Buyer.FirstName} {c.Buyer.LastName}".Trim() : "Buyer",
            BuyerEmail = c.Buyer?.Email ?? string.Empty,
            SellerId = c.SellerId,
            SellerName = c.Seller != null ? $"{c.Seller.FirstName} {c.Seller.LastName}".Trim() : "Arboveya Official",
            SellerEmail = c.Seller?.Email,
            ProductId = c.ProductId,
            ProductName = c.Product?.Name,
            ProductPrice = c.Product?.Price,
            ProductImageUrl = c.Product?.ImageUrl,
            OrderId = c.OrderId,
            PayHereOrderId = c.Order?.PayHereOrderId,
            OrderStatus = c.Order?.OrderStatus,
            OrderTotalAmount = c.Order?.TotalAmount,
            OrderCreatedAt = c.Order?.CreatedAt,
            Type = c.Type,
            Subject = c.Subject,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            LastMessageAt = c.LastMessageAt,
            LastMessageText = lastMessage?.MessageText,
            LastMessageSenderId = lastMessage?.SenderId,
            UnreadCount = unreadCount,
            BuyerLastReadAt = c.BuyerLastReadAt,
            SellerLastReadAt = c.SellerLastReadAt
        };

        return dto;
    }
}
