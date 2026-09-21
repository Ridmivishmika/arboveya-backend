namespace Arboveya.Api.DTOs;

public class UserResponseDto
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {LastName}".Trim();
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Nationality { get; set; }
    public string? PhoneNumber { get; set; }
    public bool IsSellerApproved { get; set; }
    public DateTime CreatedAt { get; set; }
}
