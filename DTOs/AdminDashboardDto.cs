namespace Arboveya.Api.DTOs;

public class AdminDashboardDto
{
    public decimal TotalSales { get; set; }
    public int TotalOrders { get; set; }
    public int PendingOrdersCount { get; set; }
    public int ProcessingOrdersCount { get; set; }
    public int ShippedOrdersCount { get; set; }
    public int DeliveredOrdersCount { get; set; }
    public int CancelledOrdersCount { get; set; }
    public int TotalProducts { get; set; }
    public int LowStockProductsCount { get; set; }
    public int TotalCustomers { get; set; }
    public int PendingReviewsCount { get; set; }
    public int PendingBlogPostsCount { get; set; }
    public int PendingProductsCount { get; set; }
    public int UnreadContactMessagesCount { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
