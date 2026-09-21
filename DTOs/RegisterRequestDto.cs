using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class RegisterRequestDto
{
    [Required(ErrorMessage = "First name is required")]
    [StringLength(100, ErrorMessage = "First name cannot exceed 100 characters")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required")]
    [StringLength(100, ErrorMessage = "Last name cannot exceed 100 characters")]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    [StringLength(255, ErrorMessage = "Email cannot exceed 255 characters")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    [MinLength(6, ErrorMessage = "Password must be at least 6 characters long")]
    public string Password { get; set; } = string.Empty;

    public string? Role { get; set; } = "Customer";

    public string? Address { get; set; }

    [StringLength(100)]
    public string? Nationality { get; set; }

    [RegularExpression(@"^[0-9]+$", ErrorMessage = "Phone number must contain only numbers")]
    [StringLength(20, MinimumLength = 7, ErrorMessage = "Phone number must be between 7 and 20 digits")]
    public string? PhoneNumber { get; set; }
}
