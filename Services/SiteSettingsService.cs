using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class SiteSettingsService : ISiteSettingsService
{
    private const int SingletonId = 1;
    private readonly AppDbContext _context;
    private readonly ILogger<SiteSettingsService> _logger;

    public SiteSettingsService(AppDbContext context, ILogger<SiteSettingsService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<SiteSettingsResponseDto> GetSettingsAsync()
    {
        var settings = await _context.SiteSettings
            .FirstOrDefaultAsync(s => s.Id == SingletonId);

        if (settings == null)
        {
            // Auto-initialize if not yet seeded
            settings = new SiteSettings
            {
                Id = SingletonId,
                HomePageHeroText = "Bring Nature Indoors with Arboveya's Premium Botanical Collection",
                AboutHeroSubtitle = "Rooted in nature, inspired by wellness.",
                AboutUsContent = "Arboveya was created with a simple mission — to bring the healing power of nature to everyday life. We carefully select the finest herbs and ingredients from trusted sources to create premium wellness products that promote a healthier and balanced lifestyle.\n\nWe believe in purity, transparency, and sustainability in everything we do.",
                Mission = "To provide premium herbal wellness solutions that support healthier lifestyles worldwide.",
                Vision = "To become a trusted global herbal wellness brand.",
                FacebookLink = "https://www.facebook.com/share/1DspKR2vtm/",
                WhatsAppNumber = "0717981355",
                UpdatedAt = DateTime.UtcNow
            };

            _context.SiteSettings.Add(settings);
            await _context.SaveChangesAsync();
        }
        else
        {
            bool modified = false;
            if (settings.WhatsAppNumber == "+94771234567" || string.IsNullOrWhiteSpace(settings.WhatsAppNumber))
            {
                settings.WhatsAppNumber = "0717981355";
                modified = true;
            }
            if (settings.FacebookLink == "https://facebook.com/arboveya" || string.IsNullOrWhiteSpace(settings.FacebookLink))
            {
                settings.FacebookLink = "https://www.facebook.com/share/1DspKR2vtm/";
                modified = true;
            }
            if (modified)
            {
                settings.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }
        }

        return MapToDto(settings);
    }

    public async Task<SiteSettingsResponseDto> UpdateSettingsAsync(UpdateSiteSettingsDto dto)
    {
        var settings = await _context.SiteSettings
            .FirstOrDefaultAsync(s => s.Id == SingletonId);

        if (settings == null)
        {
            settings = new SiteSettings
            {
                Id = SingletonId,
                UpdatedAt = DateTime.UtcNow
            };
            _context.SiteSettings.Add(settings);
        }

        // Preserve existing values unless explicitly provided
        if (dto.HomePageHeroText != null)
        {
            settings.HomePageHeroText = string.IsNullOrWhiteSpace(dto.HomePageHeroText) 
                ? null 
                : dto.HomePageHeroText.Trim();
        }

        if (dto.AboutHeroSubtitle != null)
        {
            settings.AboutHeroSubtitle = string.IsNullOrWhiteSpace(dto.AboutHeroSubtitle)
                ? null
                : dto.AboutHeroSubtitle.Trim();
        }

        if (dto.AboutUsContent != null)
        {
            settings.AboutUsContent = string.IsNullOrWhiteSpace(dto.AboutUsContent) 
                ? null 
                : dto.AboutUsContent.Trim();
        }

        if (dto.Mission != null)
        {
            settings.Mission = string.IsNullOrWhiteSpace(dto.Mission)
                ? null
                : dto.Mission.Trim();
        }

        if (dto.Vision != null)
        {
            settings.Vision = string.IsNullOrWhiteSpace(dto.Vision)
                ? null
                : dto.Vision.Trim();
        }

        if (dto.FacebookLink != null)
        {
            settings.FacebookLink = string.IsNullOrWhiteSpace(dto.FacebookLink) 
                ? null 
                : dto.FacebookLink.Trim();
        }

        if (dto.WhatsAppNumber != null)
        {
            settings.WhatsAppNumber = string.IsNullOrWhiteSpace(dto.WhatsAppNumber) 
                ? null 
                : dto.WhatsAppNumber.Trim();
        }

        settings.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("SiteSettings singleton updated successfully.");

        return MapToDto(settings);
    }

    private static SiteSettingsResponseDto MapToDto(SiteSettings settings)
    {
        return new SiteSettingsResponseDto
        {
            Id = settings.Id,
            HomePageHeroText = settings.HomePageHeroText,
            AboutHeroSubtitle = settings.AboutHeroSubtitle,
            AboutUsContent = settings.AboutUsContent,
            Mission = settings.Mission,
            Vision = settings.Vision,
            FacebookLink = settings.FacebookLink,
            WhatsAppNumber = settings.WhatsAppNumber,
            UpdatedAt = settings.UpdatedAt
        };
    }
}

