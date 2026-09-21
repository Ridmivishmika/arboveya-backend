using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class CreateContactMessageDto
{
    [Required(ErrorMessage = "Name is required.")]
    [MaxLength(150, ErrorMessage = "Name cannot exceed 150 characters.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Invalid email format.")]
    [MaxLength(255, ErrorMessage = "Email cannot exceed 255 characters.")]
    public string Email { get; set; } = string.Empty;

    [MaxLength(30, ErrorMessage = "Phone number cannot exceed 30 characters.")]
    public string? PhoneNumber { get; set; }

    [MaxLength(50, ErrorMessage = "UserType cannot exceed 50 characters.")]
    public string? UserType { get; set; } // "Buyer", "Seller", "General"

    [Required(ErrorMessage = "Subject is required.")]
    [MaxLength(200, ErrorMessage = "Subject cannot exceed 200 characters.")]
    public string Subject { get; set; } = string.Empty;

    [Required(ErrorMessage = "Message is required.")]
    [MaxLength(4000, ErrorMessage = "Message cannot exceed 4000 characters.")]
    public string Message { get; set; } = string.Empty;
}

public class UpdateContactMessageStatusDto
{
    [MaxLength(50, ErrorMessage = "Status cannot exceed 50 characters.")]
    public string? Status { get; set; }

    public string? AdminNotes { get; set; }
}

public class ContactMessageResponseDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string UserType { get; set; } = "General";
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Status { get; set; } = "Unread";
    public string? AdminNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
