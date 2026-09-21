using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class WellnessNeedService : IWellnessNeedService
{
    private readonly AppDbContext _context;
    private readonly ILogger<WellnessNeedService> _logger;

    public WellnessNeedService(AppDbContext context, ILogger<WellnessNeedService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<WellnessNeedResponseDto>> GetAllAsync(string? search = null, bool onlyWithProducts = false)
    {
        var query = _context.WellnessNeeds
            .Include(w => w.Products)
            .AsNoTracking()
            .AsQueryable();

        if (onlyWithProducts)
        {
            query = query.Where(w => w.Products.Any(p => p.ApprovalStatus.ToLower() == "approved"));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(w => EF.Functions.ILike(w.Name, $"%{trimmedSearch}%") || 
                                     (w.Description != null && EF.Functions.ILike(w.Description, $"%{trimmedSearch}%")));
        }

        var wellnessNeeds = await query
            .OrderBy(w => w.Name)
            .Select(w => MapToDto(w))
            .ToListAsync();

        return wellnessNeeds;
    }

    public async Task<WellnessNeedResponseDto?> GetByIdAsync(Guid id)
    {
        var wellnessNeed = await _context.WellnessNeeds
            .Include(w => w.Products)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == id);

        return wellnessNeed != null ? MapToDto(wellnessNeed) : null;
    }

    public async Task<WellnessNeedResponseDto?> GetByNameAsync(string name)
    {
        var trimmedName = name.Trim();
        var wellnessNeed = await _context.WellnessNeeds.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Name.ToLower() == trimmedName.ToLower());

        return wellnessNeed != null ? MapToDto(wellnessNeed) : null;
    }

    public async Task<WellnessNeedResponseDto> CreateAsync(CreateWellnessNeedDto dto)
    {
        var trimmedName = dto.Name.Trim();

        var exists = await _context.WellnessNeeds
            .AnyAsync(w => w.Name.ToLower() == trimmedName.ToLower());

        if (exists)
        {
            throw new InvalidOperationException($"A wellness need with the name '{trimmedName}' already exists.");
        }

        var wellnessNeed = new WellnessNeed
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            Icon = string.IsNullOrWhiteSpace(dto.Icon) ? null : dto.Icon.Trim(),
            ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _context.WellnessNeeds.Add(wellnessNeed);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Wellness Need '{Name}' ({Id}) created successfully.", wellnessNeed.Name, wellnessNeed.Id);

        return MapToDto(wellnessNeed);
    }

    public async Task<WellnessNeedResponseDto?> UpdateAsync(Guid id, UpdateWellnessNeedDto dto)
    {
        var wellnessNeed = await _context.WellnessNeeds.FindAsync(id);
        if (wellnessNeed == null)
        {
            return null;
        }

        var trimmedName = dto.Name.Trim();

        var duplicate = await _context.WellnessNeeds
            .AnyAsync(w => w.Id != id && w.Name.ToLower() == trimmedName.ToLower());

        if (duplicate)
        {
            throw new InvalidOperationException($"A wellness need with the name '{trimmedName}' already exists.");
        }

        wellnessNeed.Name = trimmedName;
        wellnessNeed.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        wellnessNeed.Icon = string.IsNullOrWhiteSpace(dto.Icon) ? null : dto.Icon.Trim();
        wellnessNeed.ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim();
        wellnessNeed.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Wellness Need '{Name}' ({Id}) updated successfully.", wellnessNeed.Name, wellnessNeed.Id);

        return MapToDto(wellnessNeed);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var wellnessNeed = await _context.WellnessNeeds.FindAsync(id);
        if (wellnessNeed == null)
        {
            return false;
        }

        _context.WellnessNeeds.Remove(wellnessNeed);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Wellness Need '{Name}' ({Id}) deleted successfully.", wellnessNeed.Name, wellnessNeed.Id);

        return true;
    }

    private static WellnessNeedResponseDto MapToDto(WellnessNeed wellnessNeed)
    {
        return new WellnessNeedResponseDto
        {
            Id = wellnessNeed.Id,
            Name = wellnessNeed.Name,
            Description = wellnessNeed.Description,
            Icon = wellnessNeed.Icon,
            ImageUrl = wellnessNeed.ImageUrl,
            ProductCount = wellnessNeed.Products?.Count(p => p.ApprovalStatus.ToLower() == "approved") ?? 0,
            CreatedAt = wellnessNeed.CreatedAt,
            UpdatedAt = wellnessNeed.UpdatedAt
        };
    }
}
