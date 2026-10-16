using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface IConversationService
{
    Task<ConversationDto> GetOrCreateProductConversationAsync(Guid buyerId, Guid productId, string? initialMessage = null);
    Task<ConversationDto> GetOrCreateOrderConversationAsync(Guid userId, Guid orderId, string? initialMessage = null);
    Task<List<ConversationDto>> GetBuyerConversationsAsync(Guid buyerId);
    Task<List<ConversationDto>> GetSellerConversationsAsync(Guid sellerId);
    Task<List<ConversationDto>> GetAllConversationsForAdminAsync(string? type = null, bool? unreadOnly = null, string? search = null);
    Task<ConversationDto?> GetConversationByIdAsync(Guid conversationId, Guid currentUserId, string currentUserRole);
    Task<List<MessageDto>> GetMessagesAsync(Guid conversationId, Guid currentUserId, string currentUserRole);
    Task<MessageDto> SendMessageAsync(Guid conversationId, Guid senderId, string senderRole, string messageText);
    Task<bool> MarkConversationAsReadAsync(Guid conversationId, Guid currentUserId, string currentUserRole);
    Task<int> GetUnreadCountAsync(Guid currentUserId, string currentUserRole);
}
