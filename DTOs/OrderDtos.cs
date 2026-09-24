using System.ComponentModel.DataAnnotations;

namespace Arboveya.Api.DTOs;

public class CreateOrderItemDto
{
    [Required(ErrorMessage = "ProductId is required.")]
    public Guid ProductId { get; set; }

    [Required(ErrorMessage = "Quantity is required.")]
    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
    public int Quantity { get; set; }

    public string? ProductName { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? ProductImageUrl { get; set; }
}

public class CreateOrderDto
{
    [MaxLength(150, ErrorMessage = "Customer name cannot exceed 150 characters.")]
    public string? CustomerName { get; set; }

    [EmailAddress(ErrorMessage = "Invalid email format.")]
    [MaxLength(255, ErrorMessage = "Email cannot exceed 255 characters.")]
    public string? CustomerEmail { get; set; }

    public string? ShippingAddress { get; set; }

    [MaxLength(150)]
    public string? ShippingMethod { get; set; }

    public decimal? ShippingCost { get; set; }

    [Required(ErrorMessage = "Order must contain at least one item.")]
    [MinLength(1, ErrorMessage = "Order must contain at least one item.")]
    public List<CreateOrderItemDto> Items { get; set; } = new();
}

public class UpdateOrderStatusDto
{
    [MaxLength(50)]
    public string? OrderStatus { get; set; }

    [MaxLength(50)]
    public string? PaymentStatus { get; set; }
}

public class UpdateOrderTrackingDto
{
    [Required(ErrorMessage = "Tracking number is required.")]
    [MaxLength(150, ErrorMessage = "Tracking number cannot exceed 150 characters.")]
    public string TrackingNumber { get; set; } = string.Empty;

    [MaxLength(100, ErrorMessage = "Shipping carrier cannot exceed 100 characters.")]
    public string? ShippingCarrier { get; set; }

    [MaxLength(50)]
    public string? OrderStatus { get; set; } = "Shipped";
}

public class OrderItemResponseDto
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? ProductImageUrl { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal => UnitPrice * Quantity;
}

public class OrderResponseDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string ShippingAddress { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string PaymentStatus { get; set; } = string.Empty;
    public string OrderStatus { get; set; } = string.Empty;
    public string PayHereOrderId { get; set; } = string.Empty;
    public string? TrackingNumber { get; set; }
    public string? ShippingCarrier { get; set; }
    public string? ShippingMethod { get; set; }
    public decimal ShippingCost { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<OrderItemResponseDto> Items { get; set; } = new();
}
