using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface IWellnessNeedService
{
    Task<IEnumerable<WellnessNeedResponseDto>> GetAllAsync(string? search = null, bool onlyWithProducts = false);
    Task<WellnessNeedResponseDto?> GetByIdAsync(Guid id);
    Task<WellnessNeedResponseDto?> GetByNameAsync(string name);
    Task<WellnessNeedResponseDto> CreateAsync(CreateWellnessNeedDto dto);
    Task<WellnessNeedResponseDto?> UpdateAsync(Guid id, UpdateWellnessNeedDto dto);
    Task<bool> DeleteAsync(Guid id);
}
