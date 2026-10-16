using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Arboveya.Api.Models;

[Table("Conversations")]
public class Conversation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid BuyerId { get; set; }

    [ForeignKey(nameof(BuyerId))]
    public User? Buyer { get; set; }

    public Guid? SellerId { get; set; }

    [ForeignKey(nameof(SellerId))]
    public User? Seller { get; set; }

    public Guid? ProductId { get; set; }

    [ForeignKey(nameof(ProductId))]
    public Product? Product { get; set; }

    public Guid? OrderId { get; set; }

    [ForeignKey(nameof(OrderId))]
    public Order? Order { get; set; }

    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = "ProductInquiry"; // "ProductInquiry", "OrderSupport", "General"

    [Required]
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastMessageAt { get; set; }

    public DateTime? BuyerLastReadAt { get; set; }

    public DateTime? SellerLastReadAt { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
