using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class UpdateProfileDto
{
    [MaxLength(50, ErrorMessage = "First name cannot exceed 50 characters.")]
    public string? FirstName { get; set; }

    [MaxLength(50, ErrorMessage = "Last name cannot exceed 50 characters.")]
    public string? LastName { get; set; }

    [MaxLength(500, ErrorMessage = "Address cannot exceed 500 characters.")]
    public string? Address { get; set; }

    [MaxLength(50, ErrorMessage = "Nationality cannot exceed 50 characters.")]
    public string? Nationality { get; set; }

    [MaxLength(30, ErrorMessage = "Phone number cannot exceed 30 characters.")]
    [RegularExpression(@"^[0-9\+\-\s\(\)]*$", ErrorMessage = "Phone number can only contain digits, spaces, and standard phone symbols (+, -, ()).")]
    public string? PhoneNumber { get; set; }

    [MaxLength(150, ErrorMessage = "Bank name cannot exceed 150 characters.")]
    public string? BankName { get; set; }

    [MaxLength(150, ErrorMessage = "Bank account name cannot exceed 150 characters.")]
    public string? BankAccountName { get; set; }

    [MaxLength(100, ErrorMessage = "Bank account number cannot exceed 100 characters.")]
    public string? BankAccountNumber { get; set; }

    [MaxLength(100, ErrorMessage = "Bank branch cannot exceed 100 characters.")]
    public string? BankBranch { get; set; }

    [MaxLength(100, ErrorMessage = "Bank routing code cannot exceed 100 characters.")]
    public string? BankRoutingCode { get; set; }
}
