using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<WellnessNeed> WellnessNeeds => Set<WellnessNeed>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductReview> ProductReviews => Set<ProductReview>();
    public DbSet<BlogPost> BlogPosts => Set<BlogPost>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            // Ensure Email is unique
            entity.HasIndex(u => u.Email)
                  .IsUnique();

            // Store CreatedAt as UTC timestamp in Postgres
            entity.Property(u => u.CreatedAt)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<Category>(entity =>
        {
            // Ensure Name is unique
            entity.HasIndex(c => c.Name)
                  .IsUnique();

            // Store CreatedAt as UTC timestamp in Postgres
            entity.Property(c => c.CreatedAt)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<WellnessNeed>(entity =>
        {
            // Ensure Name is unique
            entity.HasIndex(w => w.Name)
                  .IsUnique();

            // Store CreatedAt as UTC timestamp in Postgres
            entity.Property(w => w.CreatedAt)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            // Unique index on Name
            entity.HasIndex(p => p.Name)
                  .IsUnique();

            // Index on CategoryId and WellnessNeedId for fast lookup
            entity.HasIndex(p => p.CategoryId);
            entity.HasIndex(p => p.WellnessNeedId);

            // Index on SellerId and ApprovalStatus
            entity.HasIndex(p => p.SellerId);
            entity.HasIndex(p => p.ApprovalStatus);

            // Precision for Price
            entity.Property(p => p.Price)
                  .HasPrecision(18, 2);

            // Category relationship (nullable for unassigned seller submissions)
            entity.HasOne(p => p.Category)
                  .WithMany(c => c.Products)
                  .HasForeignKey(p => p.CategoryId)
                  .OnDelete(DeleteBehavior.SetNull);

            // WellnessNeed relationship
            entity.HasOne(p => p.WellnessNeed)
                  .WithMany(w => w.Products)
                  .HasForeignKey(p => p.WellnessNeedId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Seller relationship
            entity.HasOne(p => p.Seller)
                  .WithMany(u => u.SellerProducts)
                  .HasForeignKey(p => p.SellerId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Default ApprovalStatus
            entity.Property(p => p.ApprovalStatus)
                  .HasDefaultValue("Approved");

            // Store CreatedAt as UTC timestamp in Postgres
            entity.Property(p => p.CreatedAt)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<ProductReview>(entity =>
        {
            // Index for querying reviews of a product
            entity.HasIndex(r => r.ProductId);

            // Index for moderation filter
            entity.HasIndex(r => r.IsApproved);

            // Cascade delete reviews when parent product is deleted
            entity.HasOne(r => r.Product)
                  .WithMany(p => p.Reviews)
                  .HasForeignKey(r => r.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Cascade delete reviews when user account is deleted
            entity.HasOne(r => r.User)
                  .WithMany(u => u.Reviews)
                  .HasForeignKey(r => r.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Default approval status to false (pending moderation)
            entity.Property(r => r.IsApproved)
                  .HasDefaultValue(false);

            // Store CreatedAt as UTC timestamp in Postgres
            entity.Property(r => r.CreatedAt)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<ProductVariant>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.HasIndex(v => v.ProductId);
            entity.Property(v => v.Price).HasPrecision(18, 2);
            entity.Property(v => v.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Cascade delete variants when parent product is deleted
            entity.HasOne(v => v.Product)
                  .WithMany(p => p.Variants)
                  .HasForeignKey(v => v.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BlogPost>(entity =>
        {
            // Index for author lookups
            entity.HasIndex(b => b.AuthorId);

            // Index for published/approval filter
            entity.HasIndex(b => b.IsApproved);

            // Index for sorting by creation date
            entity.HasIndex(b => b.CreatedAt);

            // Cascade delete blog posts when author user is deleted
            entity.HasOne(b => b.Author)
                  .WithMany(u => u.BlogPosts)
                  .HasForeignKey(b => b.AuthorId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Default approval status to false (pending admin review)
            entity.Property(b => b.IsApproved)
                  .HasDefaultValue(false);

            // Store CreatedAt as UTC timestamp in Postgres
            entity.Property(b => b.CreatedAt)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<Order>(entity =>
        {
            // Index for customer lookups
            entity.HasIndex(o => o.UserId);

            // Index and uniqueness on PayHere gateway order ID
            entity.HasIndex(o => o.PayHereOrderId)
                  .IsUnique();

            // Status and date indexes
            entity.HasIndex(o => o.OrderStatus);
            entity.HasIndex(o => o.PaymentStatus);
            entity.HasIndex(o => o.CreatedAt);

            // Precision for TotalAmount
            entity.Property(o => o.TotalAmount)
                  .HasPrecision(18, 2);

            // Nullable foreign key for User (Guest checkout support, SetNull if user deleted)
            entity.HasOne(o => o.User)
                  .WithMany(u => u.Orders)
                  .HasForeignKey(o => o.UserId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Cascade delete order items when order is removed
            entity.HasMany(o => o.Items)
                  .WithOne(i => i.Order)
                  .HasForeignKey(i => i.OrderId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Store CreatedAt as UTC timestamp in Postgres
            entity.Property(o => o.CreatedAt)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.HasIndex(i => i.OrderId);
            entity.HasIndex(i => i.ProductId);

            // Precision for historical UnitPrice
            entity.Property(i => i.UnitPrice)
                  .HasPrecision(18, 2);

            // Restrict product deletion if it has been ordered in history
            entity.HasOne(i => i.Product)
                  .WithMany(p => p.OrderItems)
                  .HasForeignKey(i => i.ProductId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SiteSettings>(entity =>
        {
            entity.HasData(new SiteSettings
            {
                Id = 1,
                HomePageHeroText = "Bring Nature Indoors with Arboveya's Premium Botanical Collection",
                AboutUsContent = "Arboveya is dedicated to bringing vibrant, healthy plants, botanical care accessories, and sustainable greenery into your homes and workplaces.",
                FacebookLink = "https://facebook.com/arboveya",
                WhatsAppNumber = "+94771234567",
                UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        modelBuilder.Entity<ContactMessage>(entity =>
        {
            entity.HasIndex(c => c.Status);
            entity.HasIndex(c => c.CreatedAt);
            entity.HasIndex(c => c.Email);
            entity.HasIndex(c => c.UserId);

            entity.HasOne(c => c.User)
                  .WithMany(u => u.ContactMessages)
                  .HasForeignKey(c => c.UserId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.Property(c => c.Status)
                  .HasDefaultValue("Unread");

            entity.Property(c => c.CreatedAt)
                  .HasDefaultValueSql("CURRENT_TIMESTAMP");
        });
    }
}
