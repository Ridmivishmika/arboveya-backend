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
    
    [MaxLength(100)]
    public string? Country { get; set; }

    [MaxLength(50)]
    public string? CustomerPhone { get; set; }

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
    public PayHereCheckoutDetailsDto? PayHereDetails { get; set; }
}

public class PayHereCheckoutDetailsDto
{
    public bool Sandbox { get; set; } = true;
    public string MerchantId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string Items { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "LKR";
    public string Hash { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string? NotifyUrl { get; set; }
}

public class PayHereNotificationDto
{
    public string? merchant_id { get; set; }
    public string? order_id { get; set; }
    public string? payment_id { get; set; }
    public string? payhere_amount { get; set; }
    public string? payhere_currency { get; set; }
    public int status_code { get; set; }
    public string? md5sig { get; set; }
    public string? custom_1 { get; set; }
    public string? custom_2 { get; set; }
    public string? status_message { get; set; }
    public string? method { get; set; }
}

public class ConfirmPaymentRequestDto
{
    public string? PayHereOrderId { get; set; }
    public string? PaymentId { get; set; }
}

