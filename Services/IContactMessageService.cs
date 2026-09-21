using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface IContactMessageService
{
    Task<ContactMessageResponseDto> SubmitMessageAsync(Guid? userId, CreateContactMessageDto dto);
    Task<IEnumerable<ContactMessageResponseDto>> GetAllMessagesAsync(string? status = null, string? search = null);
    Task<ContactMessageResponseDto?> GetByIdAsync(Guid id, bool markAsRead = true);
    Task<ContactMessageResponseDto?> UpdateStatusAsync(Guid id, UpdateContactMessageStatusDto dto);
    Task<bool> DeleteMessageAsync(Guid id);
}
