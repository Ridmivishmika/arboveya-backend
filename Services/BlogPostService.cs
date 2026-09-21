using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class BlogPostService : IBlogPostService
{
    private readonly AppDbContext _context;
    private readonly ILogger<BlogPostService> _logger;

    public BlogPostService(AppDbContext context, ILogger<BlogPostService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<BlogPostResponseDto>> GetPublishedBlogsAsync(string? search = null)
    {
        var query = _context.BlogPosts
            .Include(b => b.Author)
            .AsNoTracking()
            .Where(b => b.IsApproved && (b.Author == null || b.Author.Role != "Seller" || b.Author.IsSellerApproved));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(b => EF.Functions.ILike(b.Title, $"%{trimmedSearch}%") ||
                                     EF.Functions.ILike(b.Content, $"%{trimmedSearch}%"));
        }

        var entities = await query
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        return entities.Select(MapToDto);
    }

    public async Task<BlogPostResponseDto?> GetByIdAsync(Guid id, Guid? currentUserId = null, bool isAdmin = false)
    {
        var blog = await _context.BlogPosts
            .Include(b => b.Author)
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (blog == null)
        {
            return null;
        }

        var isPublished = blog.IsApproved && (blog.Author == null || blog.Author.Role != "Seller" || blog.Author.IsSellerApproved);

        // If not published, only author or admin can view
        if (!isPublished && !isAdmin && (currentUserId == null || blog.AuthorId != currentUserId))
        {
            return null;
        }

        return MapToDto(blog);
    }

    public async Task<IEnumerable<BlogPostResponseDto>> GetUserBlogsAsync(Guid userId)
    {
        var entities = await _context.BlogPosts
            .Include(b => b.Author)
            .AsNoTracking()
            .Where(b => b.AuthorId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        return entities.Select(MapToDto);
    }

    public async Task<IEnumerable<BlogPostResponseDto>> GetModerationListAsync(bool? isApproved = null, string? search = null)
    {
        var query = _context.BlogPosts
            .Include(b => b.Author)
            .AsNoTracking()
            .AsQueryable();

        if (isApproved.HasValue)
        {
            query = query.Where(b => b.IsApproved == isApproved.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(b => EF.Functions.ILike(b.Title, $"%{trimmedSearch}%") ||
                                     EF.Functions.ILike(b.Content, $"%{trimmedSearch}%"));
        }

        var entities = await query
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        return entities.Select(MapToDto);
    }


    public async Task<BlogPostResponseDto> CreateBlogAsync(Guid authorId, bool isAdmin, CreateBlogPostDto dto)
    {
        var author = await _context.Users.FindAsync(authorId);
        if (author == null)
        {
            throw new UnauthorizedAccessException("Author account not found.");
        }

        var blog = new BlogPost
        {
            Id = Guid.NewGuid(),
            AuthorId = authorId,
            Title = dto.Title.Trim(),
            Content = dto.Content.Trim(),
            Category = string.IsNullOrWhiteSpace(dto.Category) ? "Wellness" : dto.Category.Trim(),
            ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim(),
            IsApproved = isAdmin, // Admins auto-approved; regular users require moderation
            CreatedAt = DateTime.UtcNow
        };

        _context.BlogPosts.Add(blog);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Blog '{Title}' ({Id}) created by user {AuthorId}. Approved: {Approved}.", 
            blog.Title, blog.Id, authorId, blog.IsApproved);

        var responseDto = MapToDto(blog);
        responseDto.AuthorName = $"{author.FirstName} {author.LastName}".Trim();
        responseDto.AuthorEmail = author.Email;
        responseDto.AuthorRole = author.Role;
        responseDto.AuthorIsSellerApproved = author.IsSellerApproved;
        responseDto.IsPublished = blog.IsApproved && (author.Role != "Seller" || author.IsSellerApproved);
        return responseDto;
    }

    public async Task<BlogPostResponseDto?> UpdateBlogAsync(Guid id, Guid currentUserId, bool isAdmin, UpdateBlogPostDto dto)
    {
        var blog = await _context.BlogPosts
            .Include(b => b.Author)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (blog == null)
        {
            return null;
        }

        // Only author or admin can update
        if (!isAdmin && blog.AuthorId != currentUserId)
        {
            throw new UnauthorizedAccessException("You are not authorized to update this blog post.");
        }

        if (!string.IsNullOrWhiteSpace(dto.Title))
        {
            blog.Title = dto.Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.Content))
        {
            blog.Content = dto.Content.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.Category))
        {
            blog.Category = dto.Category.Trim();
        }

        if (dto.ImageUrl != null)
        {
            blog.ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim();
        }

        // If updated by a regular user (buyer or seller), reset approval for admin re-moderation
        if (!isAdmin)
        {
            blog.IsApproved = false;
        }

        blog.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Blog '{Title}' ({Id}) updated.", blog.Title, blog.Id);

        return MapToDto(blog);
    }

    public async Task<BlogPostResponseDto?> ModerateBlogAsync(Guid id, bool isApproved)
    {
        var blog = await _context.BlogPosts
            .Include(b => b.Author)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (blog == null)
        {
            return null;
        }

        blog.IsApproved = isApproved;
        blog.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Blog '{Title}' ({Id}) moderation status updated: IsApproved={Status}.", 
            blog.Title, blog.Id, isApproved);

        return MapToDto(blog);
    }

    public async Task<bool> DeleteBlogAsync(Guid id, Guid currentUserId, bool isAdmin)
    {
        var blog = await _context.BlogPosts.FindAsync(id);
        if (blog == null)
        {
            return false;
        }

        // Only author or admin can delete
        if (!isAdmin && blog.AuthorId != currentUserId)
        {
            throw new UnauthorizedAccessException("You are not authorized to delete this blog post.");
        }

        _context.BlogPosts.Remove(blog);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Blog '{Title}' ({Id}) deleted.", blog.Title, id);

        return true;
    }

    private static BlogPostResponseDto MapToDto(BlogPost blog)
    {
        var authorRole = blog.Author?.Role;
        var authorIsSellerApproved = blog.Author?.IsSellerApproved ?? false;
        var isPublished = blog.IsApproved && (authorRole != "Seller" || authorIsSellerApproved);

        return new BlogPostResponseDto
        {
            Id = blog.Id,
            AuthorId = blog.AuthorId,
            AuthorName = blog.Author != null ? $"{blog.Author.FirstName} {blog.Author.LastName}".Trim() : null,
            AuthorEmail = blog.Author?.Email,
            AuthorRole = authorRole,
            AuthorIsSellerApproved = authorIsSellerApproved,
            IsPublished = isPublished,
            Title = blog.Title,
            Content = blog.Content,
            Category = blog.Category ?? "Wellness",
            ImageUrl = blog.ImageUrl,
            IsApproved = blog.IsApproved,
            CreatedAt = blog.CreatedAt,
            UpdatedAt = blog.UpdatedAt
        };
    }
}

