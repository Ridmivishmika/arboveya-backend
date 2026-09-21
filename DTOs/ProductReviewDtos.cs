using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class CreateProductReviewDto
{
    [Required(ErrorMessage = "ProductId is required.")]
    public Guid ProductId { get; set; }

    [Required(ErrorMessage = "Rating is required.")]
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5 stars.")]
    public int Rating { get; set; }

    [MaxLength(2000, ErrorMessage = "Comment cannot exceed 2000 characters.")]
    public string? Comment { get; set; }
}

public class UpdateProductReviewDto
{
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5 stars.")]
    public int? Rating { get; set; }

    [MaxLength(2000, ErrorMessage = "Comment cannot exceed 2000 characters.")]
    public string? Comment { get; set; }

    [MaxLength(150)]
    public string? AuthorName { get; set; }

    public Guid? ProductId { get; set; }
}

public class ReviewModerationDto
{
    [Required(ErrorMessage = "IsApproved status is required.")]
    public bool IsApproved { get; set; }
}

public class ProductReviewResponseDto
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string? ProductName { get; set; }
    public Guid UserId { get; set; }
    public string? UserFullName { get; set; }
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public bool IsApproved { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class PublicProductReviewDto
{
    [Required(ErrorMessage = "Author name is required.")]
    [MaxLength(100)]
    public string AuthorName { get; set; } = string.Empty;

    public Guid? UserId { get; set; }

    [EmailAddress]
    public string? UserEmail { get; set; }

    [Required(ErrorMessage = "Rating is required.")]
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5 stars.")]
    public int Rating { get; set; } = 5;

    [Required(ErrorMessage = "Comment is required.")]
    [MaxLength(2000, ErrorMessage = "Comment cannot exceed 2000 characters.")]
    public string Comment { get; set; } = string.Empty;
}

