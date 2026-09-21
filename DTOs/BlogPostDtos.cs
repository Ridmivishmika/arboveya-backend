using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class CreateBlogPostDto
{
    [Required(ErrorMessage = "Blog title is required.")]
    [MaxLength(250, ErrorMessage = "Title cannot exceed 250 characters.")]
    public string Title { get; set; } = string.Empty;

    [Required(ErrorMessage = "Blog content is required.")]
    public string Content { get; set; } = string.Empty;

    [MaxLength(100, ErrorMessage = "Category cannot exceed 100 characters.")]
    public string? Category { get; set; } = "Wellness";

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }
}

public class UpdateBlogPostDto
{
    [MaxLength(250, ErrorMessage = "Title cannot exceed 250 characters.")]
    public string? Title { get; set; }

    public string? Content { get; set; }

    [MaxLength(100, ErrorMessage = "Category cannot exceed 100 characters.")]
    public string? Category { get; set; }

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }
}

public class BlogModerationDto
{
    [Required(ErrorMessage = "IsApproved status is required.")]
    public bool IsApproved { get; set; }
}

public class BlogPostResponseDto
{
    public Guid Id { get; set; }
    public Guid AuthorId { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorEmail { get; set; }
    public string? AuthorRole { get; set; }
    public bool AuthorIsSellerApproved { get; set; }
    public bool IsPublished { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Category { get; set; } = "Wellness";
    public string? ImageUrl { get; set; }
    public bool IsApproved { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

