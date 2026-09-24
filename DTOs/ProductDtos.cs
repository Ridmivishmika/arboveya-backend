using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class ProductVariantDto
{
    public Guid? Id { get; set; }
    public string Weight { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int StockQuantity { get; set; } = 0;
}

public class CreateProductDto
{
    [Required(ErrorMessage = "CategoryId is required.")]
    public Guid CategoryId { get; set; }

    public Guid? WellnessNeedId { get; set; }

    [Required(ErrorMessage = "Product name is required.")]
    [MaxLength(200, ErrorMessage = "Product name cannot exceed 200 characters.")]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Range(0.00, 99999999.99, ErrorMessage = "Price must be a positive number.")]
    public decimal Price { get; set; } = 0;

    [Range(0, int.MaxValue, ErrorMessage = "Stock quantity cannot be negative.")]
    public int StockQuantity { get; set; } = 0;

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }

    [MaxLength(50, ErrorMessage = "Weight cannot exceed 50 characters.")]
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

    public decimal ShippingCost { get; set; } = 0;

    [MaxLength(100)]
    public string? HandlingTime { get; set; }

    public string? ReturnPolicy { get; set; }
    public string? ShippingOptions { get; set; }
    public List<ProductVariantDto>? Variants { get; set; }
}

public class UpdateProductDto
{
    public Guid? CategoryId { get; set; }

    public Guid? WellnessNeedId { get; set; }

    [MaxLength(200, ErrorMessage = "Product name cannot exceed 200 characters.")]
    public string? Name { get; set; }

    public string? Description { get; set; }

    [Range(0.00, 99999999.99, ErrorMessage = "Price must be a positive number.")]
    public decimal? Price { get; set; }

    [Range(0, int.MaxValue, ErrorMessage = "Stock quantity cannot be negative.")]
    public int? StockQuantity { get; set; }

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }

    [MaxLength(50, ErrorMessage = "Weight cannot exceed 50 characters.")]
    public string? Weight { get; set; }

    public string? Ingredients { get; set; }

    public string? HowToUse { get; set; }

    public string? KeyBenefits { get; set; }

    public string? GalleryImages { get; set; }

    public bool? IsBestSeller { get; set; }

    [MaxLength(100)]
    public string? CountryOfOrigin { get; set; }

    public DateTime? ExpiryDate { get; set; }

    public DateTime? ManufactureDate { get; set; }

    [MaxLength(100)]
    public string? Condition { get; set; }

    public string? Specifications { get; set; }

    [MaxLength(150)]
    public string? ShippingMethod { get; set; }

    [MaxLength(100)]
    public string? EstimatedDeliveryTime { get; set; }

    public bool? IsFreeShipping { get; set; }

    public decimal? ShippingCost { get; set; }

    [MaxLength(100)]
    public string? HandlingTime { get; set; }

    public string? ReturnPolicy { get; set; }
    public string? ShippingOptions { get; set; }

    public List<ProductVariantDto>? Variants { get; set; }
}

public class CreateSellerProductDto
{
    public Guid? CategoryId { get; set; }

    public Guid? WellnessNeedId { get; set; }

    [Required(ErrorMessage = "Product name is required.")]
    [MaxLength(200, ErrorMessage = "Product name cannot exceed 200 characters.")]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Range(0.00, 99999999.99, ErrorMessage = "Price must be a positive number.")]
    public decimal Price { get; set; } = 0;

    [Range(0, int.MaxValue, ErrorMessage = "Stock quantity cannot be negative.")]
    public int StockQuantity { get; set; } = 0;

    [MaxLength(500, ErrorMessage = "ImageUrl cannot exceed 500 characters.")]
    public string? ImageUrl { get; set; }

    [MaxLength(50, ErrorMessage = "Weight cannot exceed 50 characters.")]
    public string? Weight { get; set; }

    public string? Ingredients { get; set; }

    public string? HowToUse { get; set; }

    public string? KeyBenefits { get; set; }

    public string? GalleryImages { get; set; }

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

    public decimal ShippingCost { get; set; } = 0;

    [MaxLength(100)]
    public string? HandlingTime { get; set; }

    public string? ReturnPolicy { get; set; }
    public string? ShippingOptions { get; set; }

    public List<ProductVariantDto>? Variants { get; set; }
}

public class ApproveProductDto
{
    [Required(ErrorMessage = "Category assignment is required for product approval.")]
    public Guid CategoryId { get; set; }

    public Guid? WellnessNeedId { get; set; }

    public string ApprovalStatus { get; set; } = "Approved"; // "Approved" or "Rejected"

    public string? AdminFeedback { get; set; }
}

public class ProductResponseDto
{
    public Guid Id { get; set; }
    public Guid? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public Guid? WellnessNeedId { get; set; }
    public string? WellnessNeedName { get; set; }
    public Guid? SellerId { get; set; }
    public string? SellerName { get; set; }
    public string ApprovalStatus { get; set; } = "Approved";
    public string? AdminFeedback { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public string? ImageUrl { get; set; }
    public string? Weight { get; set; }
    public string? Ingredients { get; set; }
    public string? HowToUse { get; set; }
    public string? KeyBenefits { get; set; }
    public string? GalleryImages { get; set; }
    public bool IsBestSeller { get; set; }
    public string? CountryOfOrigin { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public DateTime? ManufactureDate { get; set; }
    public string? Condition { get; set; }
    public string? Specifications { get; set; }
    public string? ShippingMethod { get; set; }
    public string? EstimatedDeliveryTime { get; set; }
    public bool IsFreeShipping { get; set; }
    public decimal ShippingCost { get; set; }
    public string? HandlingTime { get; set; }
    public string? ReturnPolicy { get; set; }
    public string? ShippingOptions { get; set; }
    public double AverageRating { get; set; } = 0.0;
    public int ReviewCount { get; set; } = 0;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<ProductVariantDto>? Variants { get; set; }
}

public class PaginatedProductsDto
{
    public IEnumerable<ProductResponseDto> Items { get; set; } = new List<ProductResponseDto>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}
