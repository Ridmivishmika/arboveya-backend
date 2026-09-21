using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface ICategoryService
{
    Task<IEnumerable<CategoryResponseDto>> GetAllAsync(string? search = null);
    Task<CategoryResponseDto?> GetByIdAsync(Guid id);
    Task<CategoryResponseDto?> GetByNameAsync(string name);
    Task<CategoryResponseDto> CreateAsync(CreateCategoryDto dto);
    Task<CategoryResponseDto?> UpdateAsync(Guid id, UpdateCategoryDto dto);
    Task<bool> DeleteAsync(Guid id);
}
