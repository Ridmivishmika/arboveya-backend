using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface IOrderService
{
    Task<OrderResponseDto> CreateOrderAsync(Guid? userId, CreateOrderDto dto);
    Task<OrderResponseDto?> GetByIdAsync(Guid id, Guid? currentUserId, bool isAdmin);
    Task<IEnumerable<OrderResponseDto>> GetUserOrdersAsync(Guid userId);
    Task<IEnumerable<OrderResponseDto>> GetOrdersByEmailAsync(string email);
    Task<IEnumerable<OrderResponseDto>> GetSellerOrdersAsync(Guid sellerId);
    Task<IEnumerable<OrderResponseDto>> GetAllOrdersAsync(string? orderStatus = null, string? paymentStatus = null, string? search = null);
    Task<OrderResponseDto?> UpdateOrderStatusAsync(Guid id, UpdateOrderStatusDto dto);
    Task<OrderResponseDto?> UpdateOrderTrackingAsync(Guid id, UpdateOrderTrackingDto dto);
}
