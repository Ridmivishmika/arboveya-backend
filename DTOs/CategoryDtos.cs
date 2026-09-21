using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class CreateCategoryDto
{
    [Required(ErrorMessage = "Category name is required.")]
    [MaxLength(100, ErrorMessage = "Category name cannot exceed 100 characters.")]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }
}

public class UpdateCategoryDto
{
    [Required(ErrorMessage = "Category name is required.")]
    [MaxLength(100, ErrorMessage = "Category name cannot exceed 100 characters.")]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }
}

public class CategoryResponseDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public int ProductCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
