using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface IBlogPostService
{
    Task<IEnumerable<BlogPostResponseDto>> GetPublishedBlogsAsync(string? search = null);
    Task<BlogPostResponseDto?> GetByIdAsync(Guid id, Guid? currentUserId = null, bool isAdmin = false);
    Task<IEnumerable<BlogPostResponseDto>> GetUserBlogsAsync(Guid userId);
    Task<IEnumerable<BlogPostResponseDto>> GetModerationListAsync(bool? isApproved = null, string? search = null);
    Task<BlogPostResponseDto> CreateBlogAsync(Guid authorId, bool isAdmin, CreateBlogPostDto dto);
    Task<BlogPostResponseDto?> UpdateBlogAsync(Guid id, Guid currentUserId, bool isAdmin, UpdateBlogPostDto dto);
    Task<BlogPostResponseDto?> ModerateBlogAsync(Guid id, bool isApproved);
    Task<bool> DeleteBlogAsync(Guid id, Guid currentUserId, bool isAdmin);
}
