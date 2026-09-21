using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class CreateWellnessNeedDto
{
    [Required(ErrorMessage = "Wellness need name is required.")]
    [MaxLength(100, ErrorMessage = "Wellness need name cannot exceed 100 characters.")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Description cannot exceed 500 characters.")]
    public string? Description { get; set; }

    [MaxLength(100, ErrorMessage = "Icon identifier cannot exceed 100 characters.")]
    public string? Icon { get; set; }

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }
}

public class UpdateWellnessNeedDto
{
    [Required(ErrorMessage = "Wellness need name is required.")]
    [MaxLength(100, ErrorMessage = "Wellness need name cannot exceed 100 characters.")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500, ErrorMessage = "Description cannot exceed 500 characters.")]
    public string? Description { get; set; }

    [MaxLength(100, ErrorMessage = "Icon identifier cannot exceed 100 characters.")]
    public string? Icon { get; set; }

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }
}

public class WellnessNeedResponseDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? ImageUrl { get; set; }
    public int ProductCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
