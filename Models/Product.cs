using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Arboveya.Api.Models;

[Table("Products")]
public class Product
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? CategoryId { get; set; }

    [ForeignKey(nameof(CategoryId))]
    public Category? Category { get; set; }

    public Guid? WellnessNeedId { get; set; }

    [ForeignKey(nameof(WellnessNeedId))]
    public WellnessNeed? WellnessNeed { get; set; }

    public Guid? SellerId { get; set; }

    [ForeignKey(nameof(SellerId))]
    public User? Seller { get; set; }

    [MaxLength(30)]
    public string ApprovalStatus { get; set; } = "Approved"; // "Pending", "Approved", "Rejected"

    public string? AdminFeedback { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    [Column(TypeName = "decimal(18,2)")]
    public decimal Price { get; set; }

    public int StockQuantity { get; set; } = 0;

    [MaxLength(500)]
    public string? ImageUrl { get; set; }

    [MaxLength(50)]
    public string? Weight { get; set; }

    public string? Ingredients { get; set; }

    public string? HowToUse { get; set; }

    public string? KeyBenefits { get; set; }

    public string? GalleryImages { get; set; }

    public bool IsBestSeller { get; set; } = false;

    [MaxLength(100)]
    public string? CountryOfOrigin { get; set; }

    public DateTime? ExpiryDate { get; set; }

    public DateTime? ManufactureDate { get; set; }

    [MaxLength(100)]
    public string? Condition { get; set; } = "Brand New";

    public string? Specifications { get; set; }

    [MaxLength(150)]
    public string? ShippingMethod { get; set; }

    [MaxLength(100)]
    public string? EstimatedDeliveryTime { get; set; }

    public bool IsFreeShipping { get; set; } = false;

    [Column(TypeName = "decimal(18,2)")]
    public decimal ShippingCost { get; set; } = 0;

    [MaxLength(100)]
    public string? HandlingTime { get; set; }

    public string? ReturnPolicy { get; set; }
    public string? ShippingOptions { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public ICollection<ProductReview> Reviews { get; set; } = new List<ProductReview>();
    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
    public ICollection<ProductVariant> Variants { get; set; } = new List<ProductVariant>();
}
