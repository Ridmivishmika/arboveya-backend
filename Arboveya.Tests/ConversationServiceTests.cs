using Arboveya.Api.Data;
using Arboveya.Api.Models;
using Arboveya.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Arboveya.Tests;

public class ConversationServiceTests
{
    private AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private ConversationService CreateService(AppDbContext context)
    {
        var mockEmail = new Mock<IEmailService>();
        var mockConfig = new Mock<IConfiguration>();
        var mockLogger = new Mock<ILogger<ConversationService>>();

        return new ConversationService(context, mockEmail.Object, mockConfig.Object, mockLogger.Object);
    }

    [Fact]
    public async Task GetOrCreateProductConversation_CreatesNewConversation_WhenNoneExists()
    {
        using var context = CreateInMemoryDbContext();
        var service = CreateService(context);

        var buyer = new User
        {
            Id = Guid.NewGuid(),
            FirstName = "Alice",
            LastName = "Customer",
            Email = "alice@example.com",
            Role = "Customer"
        };
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Organic Neem Oil",
            Price = 18.50m
        };

        context.Users.Add(buyer);
        context.Products.Add(product);
        await context.SaveChangesAsync();

        var conv = await service.GetOrCreateProductConversationAsync(buyer.Id, product.Id, "Hello, is this 100% pure?");

        Assert.NotNull(conv);
        Assert.Equal(buyer.Id, conv.BuyerId);
        Assert.Equal(product.Id, conv.ProductId);
        Assert.Equal("ProductInquiry", conv.Type);
        Assert.Contains("Organic Neem Oil", conv.Subject);

        // Verify message was created
        var messages = await service.GetMessagesAsync(conv.Id, buyer.Id, "Customer");
        Assert.Single(messages);
        Assert.Equal("Hello, is this 100% pure?", messages[0].MessageText);
    }

    [Fact]
    public async Task GetOrCreateProductConversation_ReusesExistingConversation_AvoidsDuplicates()
    {
        using var context = CreateInMemoryDbContext();
        var service = CreateService(context);

        var buyerId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        context.Users.Add(new User { Id = buyerId, FirstName = "Bob", LastName = "User", Email = "bob@example.com" });
        context.Products.Add(new Product { Id = productId, Name = "Moringa Leaf Powder", Price = 22.00m });
        await context.SaveChangesAsync();

        var first = await service.GetOrCreateProductConversationAsync(buyerId, productId, "First question");
        var second = await service.GetOrCreateProductConversationAsync(buyerId, productId, "Second question");

        Assert.Equal(first.Id, second.Id);

        // Check total conversations in DB for this product and buyer
        var total = await context.Conversations.CountAsync(c => c.BuyerId == buyerId && c.ProductId == productId);
        Assert.Equal(1, total);

        var messages = await service.GetMessagesAsync(first.Id, buyerId, "Customer");
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task GetOrCreateOrderConversation_EnforcesOwnership_RejectsUnauthorizedBuyer()
    {
        using var context = CreateInMemoryDbContext();
        var service = CreateService(context);

        var ownerId = Guid.NewGuid();
        var attackerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        context.Users.Add(new User { Id = ownerId, FirstName = "Owner", LastName = "Legit", Email = "owner@example.com" });
        context.Users.Add(new User { Id = attackerId, FirstName = "Attacker", LastName = "Bad", Email = "attacker@example.com" });
        context.Orders.Add(new Order
        {
            Id = orderId,
            UserId = ownerId,
            CustomerName = "Owner Legit",
            CustomerEmail = "owner@example.com",
            ShippingAddress = "123 Street",
            TotalAmount = 50.00m,
            PayHereOrderId = "ORD-9999"
        });
        await context.SaveChangesAsync();

        // Attacker attempting to open order conversation
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await service.GetOrCreateOrderConversationAsync(attackerId, orderId, "Hello order");
        });
    }

    [Fact]
    public async Task SendMessage_RejectsEmptyOrWhitespaceMessage()
    {
        using var context = CreateInMemoryDbContext();
        var service = CreateService(context);

        var buyerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        context.Users.Add(new User { Id = buyerId, FirstName = "John", LastName = "Doe", Email = "john@example.com" });
        context.Products.Add(new Product { Id = productId, Name = "Aloe Vera Gel", Price = 12.00m });
        await context.SaveChangesAsync();

        var conv = await service.GetOrCreateProductConversationAsync(buyerId, productId);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await service.SendMessageAsync(conv.Id, buyerId, "Customer", "    ");
        });
    }

    [Fact]
    public async Task SendMessage_RejectsMessageExceeding4000Characters()
    {
        using var context = CreateInMemoryDbContext();
        var service = CreateService(context);

        var buyerId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        context.Users.Add(new User { Id = buyerId, FirstName = "John", LastName = "Doe", Email = "john@example.com" });
        context.Products.Add(new Product { Id = productId, Name = "Aloe Vera Gel", Price = 12.00m });
        await context.SaveChangesAsync();

        var conv = await service.GetOrCreateProductConversationAsync(buyerId, productId);
        var longMessage = new string('A', 4001);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await service.SendMessageAsync(conv.Id, buyerId, "Customer", longMessage);
        });
    }

    [Fact]
    public async Task Admin_CanViewAllBuyerSellerConversations()
    {
        using var context = CreateInMemoryDbContext();
        var service = CreateService(context);

        var buyer1 = new User { Id = Guid.NewGuid(), FirstName = "Buyer1", LastName = "One", Email = "b1@test.com" };
        var buyer2 = new User { Id = Guid.NewGuid(), FirstName = "Buyer2", LastName = "Two", Email = "b2@test.com" };
        var admin = new User { Id = Guid.NewGuid(), FirstName = "Admin", LastName = "Root", Email = "admin@arboveya.com", Role = "Admin" };

        var prod1 = new Product { Id = Guid.NewGuid(), Name = "Gotu Kola Leaves", Price = 15.00m };
        var prod2 = new Product { Id = Guid.NewGuid(), Name = "Ashwagandha Powder", Price = 25.00m };

        context.Users.AddRange(buyer1, buyer2, admin);
        context.Products.AddRange(prod1, prod2);
        await context.SaveChangesAsync();

        var c1 = await service.GetOrCreateProductConversationAsync(buyer1.Id, prod1.Id, "Question 1");
        var c2 = await service.GetOrCreateProductConversationAsync(buyer2.Id, prod2.Id, "Question 2");

        // Admin lists all conversations
        var allConvs = await service.GetAllConversationsForAdminAsync();
        Assert.Equal(2, allConvs.Count);

        // Admin can inspect messages in both
        var m1 = await service.GetMessagesAsync(c1.Id, admin.Id, "Admin");
        var m2 = await service.GetMessagesAsync(c2.Id, admin.Id, "Admin");
        Assert.Single(m1);
        Assert.Single(m2);

        // Buyer1 cannot inspect Buyer2's conversation
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await service.GetMessagesAsync(c2.Id, buyer1.Id, "Customer");
        });
    }

    [Fact]
    public async Task AdminReply_UpdatesUnreadCountForBuyer()
    {
        using var context = CreateInMemoryDbContext();
        var service = CreateService(context);

        var buyer = new User { Id = Guid.NewGuid(), FirstName = "Alice", LastName = "Smith", Email = "alice@test.com", Role = "Customer" };
        var admin = new User { Id = Guid.NewGuid(), FirstName = "Store", LastName = "Admin", Email = "admin@arboveya.com", Role = "Admin" };
        var prod = new Product { Id = Guid.NewGuid(), Name = "Cinnamon Bark", Price = 10.00m };

        context.Users.AddRange(buyer, admin);
        context.Products.Add(prod);
        await context.SaveChangesAsync();

        var conv = await service.GetOrCreateProductConversationAsync(buyer.Id, prod.Id, "Do you have wholesale?");

        // Admin checks unread count: should be 1
        var adminUnread = await service.GetUnreadCountAsync(admin.Id, "Admin");
        Assert.Equal(1, adminUnread);

        // Admin replies
        await service.SendMessageAsync(conv.Id, admin.Id, "Admin", "Yes, wholesale is available for bulk orders.");

        // Buyer checks unread count: should be 1
        var buyerUnread = await service.GetUnreadCountAsync(buyer.Id, "Customer");
        Assert.Equal(1, buyerUnread);

        // Buyer reads conversation
        await service.MarkConversationAsReadAsync(conv.Id, buyer.Id, "Customer");

        // Buyer unread count should now be 0
        var buyerUnreadAfter = await service.GetUnreadCountAsync(buyer.Id, "Customer");
        Assert.Equal(0, buyerUnreadAfter);
    }
}
