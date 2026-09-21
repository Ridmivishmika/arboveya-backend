using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class UpdateSiteSettingsDto
{
    [MaxLength(300, ErrorMessage = "Hero text cannot exceed 300 characters.")]
    public string? HomePageHeroText { get; set; }

    [MaxLength(300, ErrorMessage = "About Hero Subtitle cannot exceed 300 characters.")]
    public string? AboutHeroSubtitle { get; set; }

    public string? AboutUsContent { get; set; }

    public string? Mission { get; set; }

    public string? Vision { get; set; }

    [MaxLength(500, ErrorMessage = "Facebook link cannot exceed 500 characters.")]
    public string? FacebookLink { get; set; }

    [MaxLength(50, ErrorMessage = "WhatsApp number cannot exceed 50 characters.")]
    public string? WhatsAppNumber { get; set; }
}

public class SiteSettingsResponseDto
{
    public int Id { get; set; }
    public string? HomePageHeroText { get; set; }
    public string? AboutHeroSubtitle { get; set; }
    public string? AboutUsContent { get; set; }
    public string? Mission { get; set; }
    public string? Vision { get; set; }
    public string? FacebookLink { get; set; }
    public string? WhatsAppNumber { get; set; }
    public DateTime UpdatedAt { get; set; }
}

