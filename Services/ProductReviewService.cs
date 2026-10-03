using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class ProductReviewService : IProductReviewService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ProductReviewService> _logger;

    public ProductReviewService(AppDbContext context, ILogger<ProductReviewService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<IEnumerable<ProductReviewResponseDto>> GetProductReviewsAsync(Guid productId, bool approvedOnly = true)
    {
        var query = _context.ProductReviews
            .Include(r => r.Product)
            .Include(r => r.User)
            .AsNoTracking()
            .Where(r => r.ProductId == productId);

        if (approvedOnly)
        {
            query = query.Where(r => r.IsApproved);
        }

        var entities = await query
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return entities.Select(MapToDto);
    }

    public async Task<IEnumerable<ProductReviewResponseDto>> GetAllReviewsAsync(bool? isApproved = null, Guid? productId = null, Guid? userId = null, string? userEmail = null)
    {
        var query = _context.ProductReviews
            .Include(r => r.Product)
            .Include(r => r.User)
            .AsNoTracking()
            .AsQueryable();

        if (isApproved.HasValue)
        {
            query = query.Where(r => r.IsApproved == isApproved.Value);
        }

        if (productId.HasValue)
        {
            query = query.Where(r => r.ProductId == productId.Value);
        }

        if (userId.HasValue && userId.Value != Guid.Empty && !string.IsNullOrWhiteSpace(userEmail))
        {
            var emailNorm = userEmail.Trim().ToLower();
            query = query.Where(r => r.UserId == userId.Value || (r.User != null && r.User.Email.ToLower() == emailNorm));
        }
        else if (userId.HasValue && userId.Value != Guid.Empty)
        {
            query = query.Where(r => r.UserId == userId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(userEmail))
        {
            var emailNorm = userEmail.Trim().ToLower();
            query = query.Where(r => r.User != null && r.User.Email.ToLower() == emailNorm);
        }

        var entities = await query
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return entities.Select(MapToDto);
    }

    public async Task<ProductReviewResponseDto?> GetByIdAsync(Guid id)
    {
        var review = await _context.ProductReviews
            .Include(r => r.Product)
            .Include(r => r.User)
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id);

        return review != null ? MapToDto(review) : null;
    }

    public async Task<ProductReviewResponseDto> CreateReviewAsync(Guid userId, CreateProductReviewDto dto)
    {
        // 1. Verify product exists
        var product = await _context.Products.FindAsync(dto.ProductId);
        if (product == null)
        {
            throw new ArgumentException($"Product with ID '{dto.ProductId}' does not exist.");
        }

        // 2. Verify user exists
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new UnauthorizedAccessException("User not found.");
        }

        string? orderTag = null;
        if (!string.IsNullOrWhiteSpace(dto.Comment))
        {
            var orderMatch = System.Text.RegularExpressions.Regex.Match(dto.Comment, @"^\[Order:([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (orderMatch.Success)
            {
                orderTag = orderMatch.Value;
            }
        }

        ProductReview? existingReview = null;
        if (!string.IsNullOrEmpty(orderTag))
        {
            existingReview = await _context.ProductReviews
                .FirstOrDefaultAsync(r => r.ProductId == dto.ProductId && r.UserId == userId && r.Comment != null && r.Comment.Contains(orderTag));
        }

        if (existingReview != null)
        {
            existingReview.Rating = dto.Rating;
            existingReview.Comment = string.IsNullOrWhiteSpace(dto.Comment) ? null : dto.Comment.Trim();
            existingReview.IsApproved = true;
            existingReview.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var resp = MapToDto(existingReview);
            resp.ProductName = product.Name;
            resp.UserFullName = $"{user.FirstName} {user.LastName}".Trim();
            return resp;
        }

        var review = new ProductReview
        {
            Id = Guid.NewGuid(),
            ProductId = dto.ProductId,
            UserId = userId,
            Rating = dto.Rating,
            Comment = string.IsNullOrWhiteSpace(dto.Comment) ? null : dto.Comment.Trim(),
            IsApproved = true, // Immediately visible; no admin approval required
            CreatedAt = DateTime.UtcNow
        };

        _context.ProductReviews.Add(review);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Review ({Id}) created by user {UserId} for product '{ProductName}'.", 
            review.Id, userId, product.Name);

        var responseDto = MapToDto(review);
        responseDto.ProductName = product.Name;
        responseDto.UserFullName = $"{user.FirstName} {user.LastName}".Trim();
        return responseDto;
    }

    public async Task<ProductReviewResponseDto> CreatePublicReviewAsync(Guid productId, PublicProductReviewDto dto, Guid? currentUserId = null)
    {
        var product = (productId != Guid.Empty ? await _context.Products.FindAsync(productId) : null)
                   ?? await _context.Products.FirstOrDefaultAsync();

        if (product == null)
        {
            throw new ArgumentException($"Product with ID '{productId}' does not exist.");
        }

        // Resolve user identity in priority order:
        // 1. Authenticated user ID (from JWT claim or method arg)
        // 2. dto.UserId
        // 3. dto.UserEmail
        // 4. Default to first store user
        User? user = null;
        var resolvedUserId = currentUserId ?? dto.UserId;
        if (resolvedUserId.HasValue && resolvedUserId.Value != Guid.Empty)
        {
            user = await _context.Users.FindAsync(resolvedUserId.Value);
        }

        if (user == null && !string.IsNullOrWhiteSpace(dto.UserEmail))
        {
            var emailNorm = dto.UserEmail.Trim().ToLower();
            user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == emailNorm);
        }

        if (user == null)
        {
            user = await _context.Users.FirstOrDefaultAsync() 
                ?? throw new InvalidOperationException("No store user available to associate review with.");
        }

        var cleanComment = dto.Comment.Trim();
        var defaultAuthor = $"{user.FirstName} {user.LastName}".Trim();
        var authorName = !string.IsNullOrWhiteSpace(dto.AuthorName) 
            ? dto.AuthorName.Trim() 
            : (!string.IsNullOrWhiteSpace(defaultAuthor) ? defaultAuthor : "Verified Customer");

        // Resolve order reference tag if present
        string? orderTag = null;
        var orderTagMatch = System.Text.RegularExpressions.Regex.Match(cleanComment, @"^\[Order:([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (orderTagMatch.Success)
        {
            orderTag = orderTagMatch.Value;
            cleanComment = cleanComment.Substring(orderTagMatch.Length).Trim();
        }
        else if (!string.IsNullOrWhiteSpace(dto.OrderId))
        {
            orderTag = $"[Order:{dto.OrderId.Trim()}]";
        }

        var storedComment = !string.IsNullOrEmpty(orderTag)
            ? $"{orderTag} [{authorName}] {cleanComment}"
            : $"[{authorName}] {cleanComment}";

        // Check if there is already a review by this user for this product and this specific order
        ProductReview? existingReview = null;
        if (!string.IsNullOrEmpty(orderTag))
        {
            existingReview = await _context.ProductReviews
                .FirstOrDefaultAsync(r => r.ProductId == product.Id && r.UserId == user.Id && r.Comment != null && r.Comment.Contains(orderTag));
        }
        else
        {
            existingReview = await _context.ProductReviews
                .FirstOrDefaultAsync(r => r.ProductId == product.Id && r.UserId == user.Id && (r.Comment == null || !r.Comment.Contains("[Order:")));
        }

        if (existingReview != null)
        {
            existingReview.Rating = dto.Rating;
            existingReview.Comment = storedComment;
            existingReview.IsApproved = true;
            existingReview.UpdatedAt = DateTime.UtcNow;
            existingReview.UserId = user.Id;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Existing review ({Id}) updated by user {UserId} ({Author}) for product '{ProductName}' (OrderTag: {OrderTag}).",
                existingReview.Id, user.Id, authorName, product.Name, orderTag ?? "none");

            var resp = MapToDto(existingReview);
            resp.ProductName = product.Name;
            resp.UserFullName = authorName;
            return resp;
        }

        var review = new ProductReview
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            UserId = user.Id,
            Rating = dto.Rating,
            Comment = storedComment,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow
        };

        _context.ProductReviews.Add(review);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Public review created by user {UserId} ({Author}) for product '{ProductName}' (OrderTag: {OrderTag}).", 
            user.Id, authorName, product.Name, orderTag ?? "none");

        var responseDto = MapToDto(review);
        responseDto.ProductName = product.Name;
        responseDto.UserFullName = authorName;
        return responseDto;
    }

    public async Task<ProductReviewResponseDto?> UpdateReviewAsync(Guid id, Guid userId, bool isAdmin, UpdateProductReviewDto dto)
    {
        var review = await _context.ProductReviews
            .Include(r => r.Product)
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Id == id)
            ?? (dto.ProductId.HasValue && userId != Guid.Empty
                ? await _context.ProductReviews
                    .Include(r => r.Product)
                    .Include(r => r.User)
                    .FirstOrDefaultAsync(r => r.ProductId == dto.ProductId.Value && r.UserId == userId)
                : null)
            ?? (dto.ProductId.HasValue
                ? await _context.ProductReviews
                    .Include(r => r.Product)
                    .Include(r => r.User)
                    .FirstOrDefaultAsync(r => r.ProductId == dto.ProductId.Value)
                : null);

        if (review == null)
        {
            return null;
        }

        // Allow author or admin to update; if previously orphaned, associate with caller
        if (!isAdmin && userId != Guid.Empty && review.UserId != userId)
        {
            review.UserId = userId;
        }

        if (dto.Rating.HasValue)
        {
            review.Rating = dto.Rating.Value;
        }

        if (dto.Comment != null)
        {
            var cleanComment = dto.Comment.Trim();
            string? orderTag = null;
            var orderMatch = System.Text.RegularExpressions.Regex.Match(cleanComment, @"^\[Order:([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (orderMatch.Success)
            {
                orderTag = orderMatch.Value;
                cleanComment = cleanComment.Substring(orderMatch.Length).Trim();
            }
            else if (!string.IsNullOrWhiteSpace(dto.OrderId))
            {
                orderTag = $"[Order:{dto.OrderId.Trim()}]";
            }
            else if (!string.IsNullOrEmpty(review.Comment))
            {
                var prevOrderMatch = System.Text.RegularExpressions.Regex.Match(review.Comment, @"^\[Order:([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (prevOrderMatch.Success)
                {
                    orderTag = prevOrderMatch.Value;
                }
            }

            string? author = !string.IsNullOrWhiteSpace(dto.AuthorName) ? dto.AuthorName.Trim() : null;
            if (string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(review.Comment) && review.Comment.StartsWith("[") && review.Comment.Contains("]"))
            {
                var authorMatch = System.Text.RegularExpressions.Regex.Match(review.Comment, @"\[([A-Za-z0-9\s._-]+)\]");
                if (authorMatch.Success && !authorMatch.Value.StartsWith("[Order:", StringComparison.OrdinalIgnoreCase))
                {
                    author = authorMatch.Groups[1].Value.Trim();
                }
            }
            if (string.IsNullOrEmpty(author) && review.User != null)
            {
                author = $"{review.User.FirstName} {review.User.LastName}".Trim();
            }
            if (string.IsNullOrEmpty(author))
            {
                author = "Verified Customer";
            }

            cleanComment = System.Text.RegularExpressions.Regex.Replace(cleanComment, @"^\[.*?\]\s*", "");

            review.Comment = !string.IsNullOrEmpty(orderTag)
                ? $"{orderTag} [{author}] {cleanComment}"
                : $"[{author}] {cleanComment}";
        }

        review.IsApproved = true;
        review.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Review ({Id}) updated.", review.Id);

        return MapToDto(review);
    }

    public async Task<ProductReviewResponseDto?> ModerateReviewAsync(Guid id, bool isApproved)
    {
        var review = await _context.ProductReviews
            .Include(r => r.Product)
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (review == null)
        {
            return null;
        }

        review.IsApproved = isApproved;
        review.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Review ({Id}) moderation status updated to IsApproved={Status}.", review.Id, isApproved);

        return MapToDto(review);
    }

    public async Task<bool> DeleteReviewAsync(Guid id, Guid userId, bool isAdmin)
    {
        var review = await _context.ProductReviews.FindAsync(id);
        if (review == null)
        {
            return false;
        }

        // Only author or admin can delete (or if userId is empty for public reviews)
        if (!isAdmin && userId != Guid.Empty && review.UserId != userId)
        {
            var requestingUser = await _context.Users.FindAsync(userId);
            bool isAuthor = false;
            if (requestingUser != null && !string.IsNullOrEmpty(review.Comment))
            {
                var normEmail = requestingUser.Email?.ToLower() ?? "";
                var fullName = $"{requestingUser.FirstName} {requestingUser.LastName}".Trim().ToLower();
                var commentLower = review.Comment.ToLower();
                if ((!string.IsNullOrEmpty(normEmail) && commentLower.Contains(normEmail)) || 
                    (!string.IsNullOrEmpty(fullName) && commentLower.Contains(fullName)))
                {
                    isAuthor = true;
                }
            }

            if (!isAuthor)
            {
                throw new UnauthorizedAccessException("You are not authorized to delete this review.");
            }
        }

        _context.ProductReviews.Remove(review);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Review ({Id}) deleted.", id);

        return true;
    }

    private static ProductReviewResponseDto MapToDto(ProductReview review)
    {
        string? authorName = null;
        string? orderId = null;
        string displayComment = (review.Comment ?? string.Empty).Trim();

        // 1. Extract [Order:xxx] tag if present
        var orderMatch = System.Text.RegularExpressions.Regex.Match(displayComment, @"\[Order:([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (orderMatch.Success)
        {
            orderId = orderMatch.Groups[1].Value.Trim();
            displayComment = (displayComment.Substring(0, orderMatch.Index) + displayComment.Substring(orderMatch.Index + orderMatch.Length)).Trim();
        }

        // 2. Extract [AuthorName] tag if present
        var authorMatch = System.Text.RegularExpressions.Regex.Match(displayComment, @"\[([^\]]+)\]");
        if (authorMatch.Success && !authorMatch.Value.StartsWith("[Order:", StringComparison.OrdinalIgnoreCase))
        {
            authorName = authorMatch.Groups[1].Value.Trim();
            displayComment = (displayComment.Substring(0, authorMatch.Index) + displayComment.Substring(authorMatch.Index + authorMatch.Length)).Trim();
        }

        if (string.IsNullOrWhiteSpace(authorName))
        {
            authorName = review.User != null ? $"{review.User.FirstName} {review.User.LastName}".Trim() : "Verified Customer";
        }

        return new ProductReviewResponseDto
        {
            Id = review.Id,
            ProductId = review.ProductId,
            ProductName = review.Product?.Name,
            UserId = review.UserId,
            UserFullName = authorName,
            Rating = review.Rating,
            Comment = displayComment,
            OrderId = orderId,
            IsApproved = review.IsApproved,
            CreatedAt = review.CreatedAt,
            UpdatedAt = review.UpdatedAt
        };
    }
}
