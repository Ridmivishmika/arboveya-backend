using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class CreateMessageDto
{
    [Required(ErrorMessage = "Message text is required.")]
    [MaxLength(4000, ErrorMessage = "Message cannot exceed 4000 characters.")]
    public string MessageText { get; set; } = string.Empty;
}

public class CreateConversationDto
{
    [MaxLength(200, ErrorMessage = "Subject cannot exceed 200 characters.")]
    public string? Subject { get; set; }

    [MaxLength(4000, ErrorMessage = "Initial message cannot exceed 4000 characters.")]
    public string? InitialMessage { get; set; }
}

public class MessageDto
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string SenderRole { get; set; } = string.Empty;
    public string MessageText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public bool IsFromBuyer { get; set; }
    public bool IsMine { get; set; }
}

public class ConversationDto
{
    public Guid Id { get; set; }
    public Guid BuyerId { get; set; }
    public string BuyerName { get; set; } = string.Empty;
    public string BuyerEmail { get; set; } = string.Empty;

    public Guid? SellerId { get; set; }
    public string? SellerName { get; set; }
    public string? SellerEmail { get; set; }

    public Guid? ProductId { get; set; }
    public string? ProductName { get; set; }
    public decimal? ProductPrice { get; set; }
    public string? ProductImageUrl { get; set; }

    public Guid? OrderId { get; set; }
    public string? PayHereOrderId { get; set; }
    public string? OrderStatus { get; set; }
    public decimal? OrderTotalAmount { get; set; }
    public DateTime? OrderCreatedAt { get; set; }

    public string Type { get; set; } = "ProductInquiry";
    public string Subject { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public string? LastMessageText { get; set; }
    public Guid? LastMessageSenderId { get; set; }

    public int UnreadCount { get; set; }
    public DateTime? BuyerLastReadAt { get; set; }
    public DateTime? SellerLastReadAt { get; set; }

    public List<MessageDto> Messages { get; set; } = new();
}

public class UnreadCountDto
{
    public int UnreadCount { get; set; }
}
