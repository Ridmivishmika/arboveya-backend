using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface IProductService
{
    Task<PaginatedProductsDto> GetAllAsync(
        string? search = null, 
        Guid? categoryId = null,
        Guid? wellnessNeedId = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        int? minRating = null,
        string? sortBy = null,
        int page = 1,
        int pageSize = 50);

    Task<ProductResponseDto?> GetByIdAsync(Guid id);
    Task<ProductResponseDto?> GetByNameAsync(string name);
    Task<IEnumerable<ProductResponseDto>> GetRelatedAsync(Guid productId, int limit = 4);
    Task<ProductResponseDto> CreateAsync(CreateProductDto dto);
    Task<ProductResponseDto?> UpdateAsync(Guid id, UpdateProductDto dto);
    Task<bool> DeleteAsync(Guid id);

    // Seller & Approval Operations
    Task<IEnumerable<ProductResponseDto>> GetSellerProductsAsync(Guid sellerId);
    Task<IEnumerable<ProductResponseDto>> GetPendingProductsAsync();
    Task<ProductResponseDto> CreateSellerProductAsync(Guid sellerId, CreateSellerProductDto dto);
    Task<ProductResponseDto?> ApproveProductAsync(Guid id, ApproveProductDto dto);
}
