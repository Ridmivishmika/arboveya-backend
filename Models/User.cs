using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Arboveya.Api.Models;

[Table("Users")]
public class User
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string PasswordHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Role { get; set; } = "Customer";

    public string? Address { get; set; }

    [MaxLength(100)]
    public string? Nationality { get; set; }

    [MaxLength(30)]
    public string? PhoneNumber { get; set; }

    public bool IsSellerApproved { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ProductReview> Reviews { get; set; } = new List<ProductReview>();
    public ICollection<BlogPost> BlogPosts { get; set; } = new List<BlogPost>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
    public ICollection<ContactMessage> ContactMessages { get; set; } = new List<ContactMessage>();
    public ICollection<Product> SellerProducts { get; set; } = new List<Product>();
}
