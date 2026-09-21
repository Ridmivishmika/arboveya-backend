using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface ISiteSettingsService
{
    Task<SiteSettingsResponseDto> GetSettingsAsync();
    Task<SiteSettingsResponseDto> UpdateSettingsAsync(UpdateSiteSettingsDto dto);
}
