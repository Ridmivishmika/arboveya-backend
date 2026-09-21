using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class CategoryService : ICategoryService
{
    private readonly AppDbContext _context;
    private readonly ILogger<CategoryService> _logger;

    public CategoryService(AppDbContext context, ILogger<CategoryService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<CategoryResponseDto>> GetAllAsync(string? search = null)
    {
        var query = _context.Categories
            .Include(c => c.Products)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(c => EF.Functions.ILike(c.Name, $"%{trimmedSearch}%") || 
                                     (c.Description != null && EF.Functions.ILike(c.Description, $"%{trimmedSearch}%")));
        }

        var categories = await query
            .OrderBy(c => c.Name)
            .Select(c => MapToDto(c))
            .ToListAsync();

        return categories;
    }

    public async Task<CategoryResponseDto?> GetByIdAsync(Guid id)
    {
        var category = await _context.Categories
            .Include(c => c.Products)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);

        return category != null ? MapToDto(category) : null;
    }

    public async Task<CategoryResponseDto?> GetByNameAsync(string name)
    {
        var trimmedName = name.Trim();
        var category = await _context.Categories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name.ToLower() == trimmedName.ToLower());

        return category != null ? MapToDto(category) : null;
    }

    public async Task<CategoryResponseDto> CreateAsync(CreateCategoryDto dto)
    {
        var trimmedName = dto.Name.Trim();

        var exists = await _context.Categories
            .AnyAsync(c => c.Name.ToLower() == trimmedName.ToLower());

        if (exists)
        {
            throw new InvalidOperationException($"A category with the name '{trimmedName}' already exists.");
        }

        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _context.Categories.Add(category);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Category '{Name}' ({Id}) created successfully.", category.Name, category.Id);

        return MapToDto(category);
    }

    public async Task<CategoryResponseDto?> UpdateAsync(Guid id, UpdateCategoryDto dto)
    {
        var category = await _context.Categories.FindAsync(id);
        if (category == null)
        {
            return null;
        }

        var trimmedName = dto.Name.Trim();

        var duplicate = await _context.Categories
            .AnyAsync(c => c.Id != id && c.Name.ToLower() == trimmedName.ToLower());

        if (duplicate)
        {
            throw new InvalidOperationException($"A category with the name '{trimmedName}' already exists.");
        }

        category.Name = trimmedName;
        category.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        category.ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim();
        category.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Category '{Name}' ({Id}) updated successfully.", category.Name, category.Id);

        return MapToDto(category);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var category = await _context.Categories.FindAsync(id);
        if (category == null)
        {
            return false;
        }

        _context.Categories.Remove(category);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Category '{Name}' ({Id}) deleted successfully.", category.Name, category.Id);

        return true;
    }

    private static CategoryResponseDto MapToDto(Category category)
    {
        return new CategoryResponseDto
        {
            Id = category.Id,
            Name = category.Name,
            Description = category.Description,
            ImageUrl = category.ImageUrl,
            ProductCount = category.Products?.Count ?? 0,
            CreatedAt = category.CreatedAt,
            UpdatedAt = category.UpdatedAt
        };
    }
}
