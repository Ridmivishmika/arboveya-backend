using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class ProductService : IProductService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ProductService> _logger;

    public ProductService(AppDbContext context, ILogger<ProductService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<PaginatedProductsDto> GetAllAsync(
        string? search = null, 
        Guid? categoryId = null,
        Guid? wellnessNeedId = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        int? minRating = null,
        string? sortBy = null,
        int page = 1,
        int pageSize = 50)
    {
        var query = _context.Products
            .Include(p => p.Category)
            .Include(p => p.WellnessNeed)
            .Include(p => p.Seller)
            .Include(p => p.Reviews)
            .Include(p => p.Variants)
            .AsNoTracking()
            .Where(p => p.ApprovalStatus == "Approved")
            .AsQueryable();

        if (categoryId.HasValue)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        if (wellnessNeedId.HasValue)
        {
            query = query.Where(p => p.WellnessNeedId == wellnessNeedId.Value);
        }

        if (minPrice.HasValue)
        {
            query = query.Where(p => p.Price >= minPrice.Value);
        }

        if (maxPrice.HasValue)
        {
            query = query.Where(p => p.Price <= maxPrice.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{trimmedSearch}%") ||
                                     (p.Description != null && EF.Functions.ILike(p.Description, $"%{trimmedSearch}%")) ||
                                     (p.Ingredients != null && EF.Functions.ILike(p.Ingredients, $"%{trimmedSearch}%")) ||
                                     (p.Weight != null && EF.Functions.ILike(p.Weight, $"%{trimmedSearch}%")));
        }

        // Sorting
        query = sortBy?.ToLowerInvariant() switch
        {
            "price-low" or "price_asc" => query.OrderBy(p => p.Price),
            "price-high" or "price_desc" => query.OrderByDescending(p => p.Price),
            "alphabetical" or "name_asc" => query.OrderBy(p => p.Name),
            "newest" => query.OrderByDescending(p => p.CreatedAt),
            "featured" => query.OrderByDescending(p => p.IsBestSeller).ThenBy(p => p.Name),
            _ => query.OrderByDescending(p => p.IsBestSeller).ThenBy(p => p.Name)
        };

        var totalCount = await query.CountAsync();
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);

        var rawProducts = await query
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync();

        var items = rawProducts.Select(MapToDto).ToList();

        // Optional post-filter for rating if minRating provided
        if (minRating.HasValue)
        {
            items = items.Where(p => p.AverageRating >= minRating.Value).ToList();
        }

        return new PaginatedProductsDto
        {
            Items = items,
            TotalCount = totalCount,
            Page = safePage,
            PageSize = safePageSize,
            TotalPages = (int)Math.Ceiling((double)totalCount / safePageSize)
        };
    }

    public async Task<ProductResponseDto?> GetByIdAsync(Guid id)
    {
        var product = await _context.Products
            .Include(p => p.Category)
            .Include(p => p.WellnessNeed)
            .Include(p => p.Reviews)
            .Include(p => p.Variants)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        return product != null ? MapToDto(product) : null;
    }

    public async Task<ProductResponseDto?> GetByNameAsync(string name)
    {
        var trimmedName = name.Trim();
        var product = await _context.Products
            .Include(p => p.Category)
            .Include(p => p.WellnessNeed)
            .Include(p => p.Reviews)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name.ToLower() == trimmedName.ToLower());

        return product != null ? MapToDto(product) : null;
    }

    public async Task<IEnumerable<ProductResponseDto>> GetRelatedAsync(Guid productId, int limit = 4)
    {
        var product = await _context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId);
        if (product == null)
        {
            return Enumerable.Empty<ProductResponseDto>();
        }

        var related = await _context.Products
            .Include(p => p.Category)
            .Include(p => p.WellnessNeed)
            .Include(p => p.Reviews)
            .Include(p => p.Variants)
            .AsNoTracking()
            .Where(p => p.Id != productId && p.CategoryId == product.CategoryId && p.ApprovalStatus == "Approved")
            .OrderByDescending(p => p.IsBestSeller)
            .Take(limit)
            .ToListAsync();

        if (related.Count < limit)
        {
            var needed = limit - related.Count;
            var excludedIds = related.Select(r => r.Id).Append(productId).ToList();
            var extra = await _context.Products
                .Include(p => p.Category)
                .Include(p => p.WellnessNeed)
                .Include(p => p.Reviews)
                .Include(p => p.Variants)
                .AsNoTracking()
                .Where(p => !excludedIds.Contains(p.Id) && p.ApprovalStatus == "Approved")
                .OrderByDescending(p => p.IsBestSeller)
                .Take(needed)
                .ToListAsync();

            related.AddRange(extra);
        }

        return related.Select(MapToDto);
    }

    public async Task<ProductResponseDto> CreateAsync(CreateProductDto dto)
    {
        var category = await _context.Categories.FindAsync(dto.CategoryId);
        if (category == null)
        {
            throw new ArgumentException($"Category with ID '{dto.CategoryId}' does not exist.");
        }

        WellnessNeed? wellnessNeed = null;
        if (dto.WellnessNeedId.HasValue && dto.WellnessNeedId.Value != Guid.Empty)
        {
            wellnessNeed = await _context.WellnessNeeds.FindAsync(dto.WellnessNeedId.Value);
            if (wellnessNeed == null)
            {
                throw new ArgumentException($"Wellness need with ID '{dto.WellnessNeedId.Value}' does not exist.");
            }
        }

        var trimmedName = dto.Name.Trim();
        var exists = await _context.Products
            .AnyAsync(p => p.Name.ToLower() == trimmedName.ToLower());

        if (exists)
        {
            throw new InvalidOperationException($"A product with the name '{trimmedName}' already exists.");
        }

        // If variants provided, derive base price, weight, and stock before saving
        if (dto.Variants != null && dto.Variants.Count > 0)
        {
            var validInit = dto.Variants.Where(v => !string.IsNullOrWhiteSpace(v.Weight)).ToList();
            if (validInit.Count > 0)
            {
                var lowestInit = validInit.OrderBy(v => v.Price).First();
                dto.Price = lowestInit.Price;
                if (string.IsNullOrWhiteSpace(dto.Weight)) dto.Weight = lowestInit.Weight;
                dto.StockQuantity = validInit.Sum(v => v.StockQuantity);
            }
        }

        var product = new Product
        {
            Id = Guid.NewGuid(),
            CategoryId = dto.CategoryId,
            Category = category,
            WellnessNeedId = wellnessNeed?.Id,
            WellnessNeed = wellnessNeed,
            Name = trimmedName,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            Price = dto.Price,
            StockQuantity = dto.StockQuantity,
            ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim(),
            Weight = string.IsNullOrWhiteSpace(dto.Weight) ? null : dto.Weight.Trim(),
            Ingredients = string.IsNullOrWhiteSpace(dto.Ingredients) ? null : dto.Ingredients.Trim(),
            HowToUse = string.IsNullOrWhiteSpace(dto.HowToUse) ? null : dto.HowToUse.Trim(),
            KeyBenefits = string.IsNullOrWhiteSpace(dto.KeyBenefits) ? null : dto.KeyBenefits.Trim(),
            GalleryImages = string.IsNullOrWhiteSpace(dto.GalleryImages) ? null : dto.GalleryImages.Trim(),
            IsBestSeller = dto.IsBestSeller,
            CreatedAt = DateTime.UtcNow
        };

        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        // Handle variants
        if (dto.Variants != null && dto.Variants.Count > 0)
        {
            foreach (var v in dto.Variants)
            {
                if (string.IsNullOrWhiteSpace(v.Weight)) continue;
                _context.ProductVariants.Add(new ProductVariant
                {
                    Id = Guid.NewGuid(),
                    ProductId = product.Id,
                    Weight = v.Weight.Trim(),
                    Price = v.Price,
                    StockQuantity = v.StockQuantity,
                    CreatedAt = DateTime.UtcNow
                });
            }
            await _context.SaveChangesAsync();

            // Sync base price/stock/weight to lowest variant price and total stock
            var variants = _context.ProductVariants.Where(pv => pv.ProductId == product.Id).ToList();
            var tracked = await _context.Products.FindAsync(product.Id);
            if (tracked != null && variants.Count > 0)
            {
                var lowest = variants.OrderBy(pv => pv.Price).First();
                tracked.Price = lowest.Price;
                tracked.Weight = lowest.Weight;
                tracked.StockQuantity = variants.Sum(pv => pv.StockQuantity);
                await _context.SaveChangesAsync();
                product = tracked;
            }
        }

        _logger.LogInformation("Product '{Name}' ({Id}) created in category '{CategoryName}'.", product.Name, product.Id, category.Name);

        var responseDto = MapToDto(product);
        responseDto.CategoryName = category.Name;
        responseDto.WellnessNeedName = wellnessNeed?.Name;
        return responseDto;
    }

    public async Task<ProductResponseDto?> UpdateAsync(Guid id, UpdateProductDto dto)
    {
        var product = await _context.Products
            .Include(p => p.Category)
            .Include(p => p.WellnessNeed)
            .Include(p => p.Reviews)
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null)
        {
            return null;
        }

        if (dto.CategoryId.HasValue && dto.CategoryId.Value != product.CategoryId)
        {
            var category = await _context.Categories.FindAsync(dto.CategoryId.Value);
            if (category == null)
            {
                throw new ArgumentException($"Category with ID '{dto.CategoryId.Value}' does not exist.");
            }
            product.CategoryId = dto.CategoryId.Value;
            product.Category = category;
        }

        if (dto.WellnessNeedId.HasValue)
        {
            if (dto.WellnessNeedId.Value == Guid.Empty)
            {
                product.WellnessNeedId = null;
                product.WellnessNeed = null;
            }
            else if (dto.WellnessNeedId.Value != product.WellnessNeedId)
            {
                var wellnessNeed = await _context.WellnessNeeds.FindAsync(dto.WellnessNeedId.Value);
                if (wellnessNeed == null)
                {
                    throw new ArgumentException($"Wellness need with ID '{dto.WellnessNeedId.Value}' does not exist.");
                }
                product.WellnessNeedId = dto.WellnessNeedId.Value;
                product.WellnessNeed = wellnessNeed;
            }
        }

        if (!string.IsNullOrWhiteSpace(dto.Name))
        {
            var trimmedName = dto.Name.Trim();
            var duplicate = await _context.Products
                .AnyAsync(p => p.Id != id && p.Name.ToLower() == trimmedName.ToLower());

            if (duplicate)
            {
                throw new InvalidOperationException($"A product with the name '{trimmedName}' already exists.");
            }
            product.Name = trimmedName;
        }

        if (dto.Description != null)
        {
            product.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        }

        if (dto.Price.HasValue)
        {
            product.Price = dto.Price.Value;
        }

        if (dto.StockQuantity.HasValue)
        {
            product.StockQuantity = dto.StockQuantity.Value;
        }

        if (dto.ImageUrl != null)
        {
            product.ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim();
        }

        if (dto.Weight != null)
        {
            product.Weight = string.IsNullOrWhiteSpace(dto.Weight) ? null : dto.Weight.Trim();
        }

        if (dto.Ingredients != null)
        {
            product.Ingredients = string.IsNullOrWhiteSpace(dto.Ingredients) ? null : dto.Ingredients.Trim();
        }

        if (dto.HowToUse != null)
        {
            product.HowToUse = string.IsNullOrWhiteSpace(dto.HowToUse) ? null : dto.HowToUse.Trim();
        }

        if (dto.KeyBenefits != null)
        {
            product.KeyBenefits = string.IsNullOrWhiteSpace(dto.KeyBenefits) ? null : dto.KeyBenefits.Trim();
        }

        if (dto.GalleryImages != null)
        {
            product.GalleryImages = string.IsNullOrWhiteSpace(dto.GalleryImages) ? null : dto.GalleryImages.Trim();
        }

        if (dto.IsBestSeller.HasValue)
        {
            product.IsBestSeller = dto.IsBestSeller.Value;
        }

        product.UpdatedAt = DateTime.UtcNow;

        // Sync variants if provided
        if (dto.Variants != null)
        {
            // Remove deleted variants (those whose Id no longer appears in the updated list)
            var incomingIds = dto.Variants.Where(v => v.Id.HasValue).Select(v => v.Id!.Value).ToHashSet();
            var toDelete = product.Variants.Where(pv => !incomingIds.Contains(pv.Id)).ToList();
            _context.ProductVariants.RemoveRange(toDelete);

            foreach (var v in dto.Variants)
            {
                if (string.IsNullOrWhiteSpace(v.Weight)) continue;
                if (v.Id.HasValue)
                {
                    // Update existing
                    var existing = product.Variants.FirstOrDefault(pv => pv.Id == v.Id.Value);
                    if (existing != null)
                    {
                        existing.Weight = v.Weight.Trim();
                        existing.Price = v.Price;
                        existing.StockQuantity = v.StockQuantity;
                    }
                }
                else
                {
                    // Create new
                    _context.ProductVariants.Add(new ProductVariant
                    {
                        Id = Guid.NewGuid(),
                        ProductId = product.Id,
                        Weight = v.Weight.Trim(),
                        Price = v.Price,
                        StockQuantity = v.StockQuantity,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            await _context.SaveChangesAsync();

            // Sync base price/stock/weight to lowest variant
            var allVariants = await _context.ProductVariants.Where(pv => pv.ProductId == product.Id).ToListAsync();
            if (allVariants.Count > 0)
            {
                var lowest = allVariants.OrderBy(pv => pv.Price).First();
                product.Price = lowest.Price;
                product.Weight = lowest.Weight;
                product.StockQuantity = allVariants.Sum(pv => pv.StockQuantity);
            }
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("Product '{Name}' ({Id}) updated successfully.", product.Name, product.Id);

        var responseDto = MapToDto(product);
        responseDto.CategoryName = product.Category?.Name;
        responseDto.WellnessNeedName = product.WellnessNeed?.Name;
        return responseDto;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null)
        {
            return false;
        }

        _context.Products.Remove(product);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Product '{Name}' ({Id}) deleted successfully.", product.Name, product.Id);

        return true;
    }

    public async Task<IEnumerable<ProductResponseDto>> GetSellerProductsAsync(Guid sellerId)
    {
        var products = await _context.Products
            .Include(p => p.Category)
            .Include(p => p.WellnessNeed)
            .Include(p => p.Seller)
            .Include(p => p.Reviews)
            .Include(p => p.Variants)
            .AsNoTracking()
            .Where(p => p.SellerId == sellerId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        return products.Select(MapToDto);
    }

    public async Task<IEnumerable<ProductResponseDto>> GetPendingProductsAsync()
    {
        var products = await _context.Products
            .Include(p => p.Category)
            .Include(p => p.WellnessNeed)
            .Include(p => p.Seller)
            .Include(p => p.Reviews)
            .Include(p => p.Variants)
            .AsNoTracking()
            .Where(p => p.ApprovalStatus == "Pending")
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        return products.Select(MapToDto);
    }

    public async Task<ProductResponseDto> CreateSellerProductAsync(Guid sellerId, CreateSellerProductDto dto)
    {
        User? seller = null;
        if (sellerId != Guid.Empty)
        {
            seller = await _context.Users.FindAsync(sellerId);
        }

        if (seller == null)
        {
            // If seller account not found by direct ID (e.g. demo submission), associate with first Seller user
            seller = await _context.Users.FirstOrDefaultAsync(u => u.Role == "Seller") 
                     ?? await _context.Users.FirstOrDefaultAsync();
        }

        var trimmedName = dto.Name.Trim();
        var exists = await _context.Products
            .AnyAsync(p => p.Name.ToLower() == trimmedName.ToLower());

        if (exists)
        {
            throw new InvalidOperationException($"A product with the name '{trimmedName}' already exists.");
        }

        Guid? targetCatId = null;
        Category? targetCat = null;
        if (dto.CategoryId.HasValue && dto.CategoryId.Value != Guid.Empty)
        {
            targetCat = await _context.Categories.FindAsync(dto.CategoryId.Value);
            if (targetCat != null) targetCatId = targetCat.Id;
        }

        Guid? targetNeedId = null;
        WellnessNeed? targetNeed = null;
        if (dto.WellnessNeedId.HasValue && dto.WellnessNeedId.Value != Guid.Empty)
        {
            targetNeed = await _context.WellnessNeeds.FindAsync(dto.WellnessNeedId.Value);
            if (targetNeed != null) targetNeedId = targetNeed.Id;
        }

        var product = new Product
        {
            Id = Guid.NewGuid(),
            SellerId = seller?.Id,
            CategoryId = targetCatId,
            Category = targetCat,
            WellnessNeedId = targetNeedId,
            WellnessNeed = targetNeed,
            ApprovalStatus = "Pending", // Requires Admin approval
            Name = trimmedName,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            Price = dto.Price,
            StockQuantity = dto.StockQuantity,
            ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim(),
            Weight = string.IsNullOrWhiteSpace(dto.Weight) ? null : dto.Weight.Trim(),
            Ingredients = string.IsNullOrWhiteSpace(dto.Ingredients) ? null : dto.Ingredients.Trim(),
            HowToUse = string.IsNullOrWhiteSpace(dto.HowToUse) ? null : dto.HowToUse.Trim(),
            KeyBenefits = string.IsNullOrWhiteSpace(dto.KeyBenefits) ? null : dto.KeyBenefits.Trim(),
            GalleryImages = string.IsNullOrWhiteSpace(dto.GalleryImages) ? null : dto.GalleryImages.Trim(),
            IsBestSeller = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Products.Add(product);
        await _context.SaveChangesAsync();

        // Handle variants for seller product
        if (dto.Variants != null && dto.Variants.Count > 0)
        {
            foreach (var v in dto.Variants)
            {
                if (string.IsNullOrWhiteSpace(v.Weight)) continue;
                _context.ProductVariants.Add(new ProductVariant
                {
                    Id = Guid.NewGuid(),
                    ProductId = product.Id,
                    Weight = v.Weight.Trim(),
                    Price = v.Price,
                    StockQuantity = v.StockQuantity,
                    CreatedAt = DateTime.UtcNow
                });
            }
            await _context.SaveChangesAsync();

            var sellerVariants = _context.ProductVariants.Where(pv => pv.ProductId == product.Id).ToList();
            var trackedSeller = await _context.Products.FindAsync(product.Id);
            if (trackedSeller != null && sellerVariants.Count > 0)
            {
                var lowest = sellerVariants.OrderBy(pv => pv.Price).First();
                trackedSeller.Price = lowest.Price;
                trackedSeller.Weight = lowest.Weight;
                trackedSeller.StockQuantity = sellerVariants.Sum(pv => pv.StockQuantity);
                await _context.SaveChangesAsync();
                product = trackedSeller;
            }
        }

        var sellerDisplayName = seller != null ? $"{seller.FirstName} {seller.LastName}".Trim() : "Herbal Seller";
        _logger.LogInformation("Seller '{SellerName}' submitted product '{ProductName}' for admin review.", 
            sellerDisplayName, product.Name);

        var responseDto = MapToDto(product);
        responseDto.SellerName = sellerDisplayName;
        return responseDto;
    }

    public async Task<ProductResponseDto?> ApproveProductAsync(Guid id, ApproveProductDto dto)
    {
        var product = await _context.Products
            .Include(p => p.Category)
            .Include(p => p.WellnessNeed)
            .Include(p => p.Seller)
            .Include(p => p.Reviews)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (product == null)
        {
            return null;
        }

        var newStatus = string.IsNullOrWhiteSpace(dto.ApprovalStatus) ? "Approved" : dto.ApprovalStatus.Trim();

        // If approving, category assignment is required
        if (newStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase))
        {
            if (dto.CategoryId == Guid.Empty)
            {
                throw new ArgumentException("Please select a category to assign to this product upon approval.");
            }

            var category = await _context.Categories.FindAsync(dto.CategoryId);
            if (category == null)
            {
                throw new ArgumentException($"Category with ID '{dto.CategoryId}' does not exist.");
            }

            product.CategoryId = dto.CategoryId;
            product.Category = category;
        }
        else if (dto.CategoryId != Guid.Empty)
        {
            var category = await _context.Categories.FindAsync(dto.CategoryId);
            if (category != null)
            {
                product.CategoryId = dto.CategoryId;
                product.Category = category;
            }
        }

        // Assign or update Wellness Need upon approval
        if (dto.WellnessNeedId.HasValue && dto.WellnessNeedId.Value != Guid.Empty)
        {
            var wellnessNeed = await _context.WellnessNeeds.FindAsync(dto.WellnessNeedId.Value);
            if (wellnessNeed != null)
            {
                product.WellnessNeedId = dto.WellnessNeedId.Value;
                product.WellnessNeed = wellnessNeed;
            }
        }

        product.ApprovalStatus = newStatus;
        product.AdminFeedback = dto.AdminFeedback?.Trim();
        product.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Admin moderated product '{ProductName}' ({Id}). Status: {Status}, Category: {CategoryName}, WellnessNeed: {WellnessNeedName}.",
            product.Name, product.Id, product.ApprovalStatus, product.Category?.Name ?? "Unassigned", product.WellnessNeed?.Name ?? "Unassigned");

        var responseDto = MapToDto(product);
        responseDto.CategoryName = product.Category?.Name;
        responseDto.WellnessNeedName = product.WellnessNeed?.Name;
        return responseDto;
    }

    private static ProductResponseDto MapToDto(Product product)
    {
        var approvedReviews = product.Reviews?.Where(r => r.IsApproved).ToList() ?? new List<ProductReview>();
        var avgRating = approvedReviews.Any() ? Math.Round(approvedReviews.Average(r => r.Rating), 1) : 0.0;
        var reviewCount = approvedReviews.Count;

        var sortedVariants = product.Variants?
            .OrderBy(v => v.Price)
            .Select(v => new ProductVariantDto
            {
                Id = v.Id,
                Weight = v.Weight,
                Price = v.Price,
                StockQuantity = v.StockQuantity
            }).ToList();

        decimal finalPrice = product.Price;
        string? finalWeight = product.Weight;
        if (sortedVariants != null && sortedVariants.Count > 0)
        {
            finalPrice = sortedVariants[0].Price;
            finalWeight = sortedVariants[0].Weight;
        }

        return new ProductResponseDto
        {
            Id = product.Id,
            CategoryId = product.CategoryId,
            CategoryName = product.Category?.Name,
            WellnessNeedId = product.WellnessNeedId,
            WellnessNeedName = product.WellnessNeed?.Name,
            SellerId = product.SellerId,
            SellerName = product.Seller != null ? $"{product.Seller.FirstName} {product.Seller.LastName}".Trim() : null,
            ApprovalStatus = product.ApprovalStatus ?? "Approved",
            AdminFeedback = product.AdminFeedback,
            Name = product.Name,
            Description = product.Description,
            Price = finalPrice,
            StockQuantity = product.StockQuantity,
            ImageUrl = product.ImageUrl,
            Weight = finalWeight,
            Ingredients = product.Ingredients,
            HowToUse = product.HowToUse,
            KeyBenefits = product.KeyBenefits,
            GalleryImages = product.GalleryImages,
            IsBestSeller = product.IsBestSeller,
            AverageRating = avgRating,
            ReviewCount = reviewCount,
            CreatedAt = product.CreatedAt,
            UpdatedAt = product.UpdatedAt,
            Variants = sortedVariants
        };
    }
}
