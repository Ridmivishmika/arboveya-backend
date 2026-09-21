using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Data;

public static class DbSeeder
{
    /// <summary>
    /// Seeding disabled per user request. No automatic data is added to the database.
    /// </summary>
    public static Task SeedAsync(AppDbContext context, ILogger logger)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes any sample/seed data that was previously inserted into the database.
    /// </summary>
    public static async Task PurgeAllSeededDataAsync(AppDbContext context, ILogger logger)
    {
        try
        {
            var seedProductGuids = new[]
            {
                Guid.Parse("b7cbc9ea-29db-46b9-b87d-103b4a81694a"),
                Guid.Parse("79d62b68-e591-4afe-97e0-6abbb260e738"),
                Guid.Parse("a1c1d2e3-f4a5-4b6c-7d8e-9f0a1b2c3d4e"),
                Guid.Parse("b2c2d3e4-f5a6-4b7c-8d9e-0f1a2b3c4d5e")
            };

            // 1. Remove seeded reviews
            var seededReviews = await context.ProductReviews
                .Where(r => seedProductGuids.Contains(r.ProductId) || 
                            (r.Comment != null && r.Comment.Contains("[Verified Buyer] Absolutely remarkable quality!")))
                .ToListAsync();

            if (seededReviews.Any())
            {
                context.ProductReviews.RemoveRange(seededReviews);
                await context.SaveChangesAsync();
                logger.LogInformation("Purged {Count} seeded product reviews from database.", seededReviews.Count);
            }

            // 2. Remove seeded products
            var seededProducts = await context.Products
                .Where(p => seedProductGuids.Contains(p.Id))
                .ToListAsync();

            if (seededProducts.Any())
            {
                context.Products.RemoveRange(seededProducts);
                await context.SaveChangesAsync();
                logger.LogInformation("Purged {Count} seeded products from database.", seededProducts.Count);
            }

            // 3. Remove seeded categories created by seeder
            var seededCategoryNames = new[] 
            { 
                "Seeds & Raw Herbs", 
                "Herbal Teas & Infusions", 
                "Capsules & Supplements", 
                "Botanical Extracts & Tonics", 
                "Herbal Oils & Elixirs" 
            };

            var seededCategories = await context.Categories
                .Where(c => seededCategoryNames.Contains(c.Name))
                .ToListAsync();

            if (seededCategories.Any())
            {
                context.Categories.RemoveRange(seededCategories);
                await context.SaveChangesAsync();
                logger.LogInformation("Purged {Count} seeded categories from database.", seededCategories.Count);
            }

            // 4. Remove seeded system admin if created by seeder
            var seededAdmin = await context.Users
                .Where(u => u.Email == "admin@arboveya.com" && u.FirstName == "System" && u.LastName == "Admin")
                .ToListAsync();

            if (seededAdmin.Any())
            {
                context.Users.RemoveRange(seededAdmin);
                await context.SaveChangesAsync();
                logger.LogInformation("Purged seeded system administrator account from database.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error occurred while purging seeded data from database.");
        }
    }
}
