using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface IProductReviewService
{
    Task<IEnumerable<ProductReviewResponseDto>> GetProductReviewsAsync(Guid productId, bool approvedOnly = true);
    Task<IEnumerable<ProductReviewResponseDto>> GetAllReviewsAsync(bool? isApproved = null, Guid? productId = null, Guid? userId = null, string? userEmail = null);
    Task<ProductReviewResponseDto?> GetByIdAsync(Guid id);
    Task<ProductReviewResponseDto> CreateReviewAsync(Guid userId, CreateProductReviewDto dto);
    Task<ProductReviewResponseDto> CreatePublicReviewAsync(Guid productId, PublicProductReviewDto dto, Guid? currentUserId = null);
    Task<ProductReviewResponseDto?> UpdateReviewAsync(Guid id, Guid userId, bool isAdmin, UpdateProductReviewDto dto);
    Task<ProductReviewResponseDto?> ModerateReviewAsync(Guid id, bool isApproved);
    Task<bool> DeleteReviewAsync(Guid id, Guid userId, bool isAdmin);
}
