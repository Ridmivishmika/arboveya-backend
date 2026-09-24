using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class AuthService : IAuthService
{
    private readonly AppDbContext _context;
    private readonly ITokenService _tokenService;
    private readonly ILogger<AuthService> _logger;
    private readonly IEmailService _emailService;

    public AuthService(AppDbContext context, ITokenService tokenService, ILogger<AuthService> logger, IEmailService emailService)
    {
        _context = context;
        _tokenService = tokenService;
        _logger = logger;
        _emailService = emailService;
    }

    public async Task<AuthResponseDto> RegisterAsync(RegisterRequestDto request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        // Check if user already exists
        var existingUser = await _context.Users
            .AnyAsync(u => u.Email.ToLower() == normalizedEmail);

        if (existingUser)
        {
            throw new InvalidOperationException("An account with this email address already exists.");
        }

        // Validate phone number contains only numeric digits if provided
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber) && !request.PhoneNumber.Trim().All(char.IsDigit))
        {
            throw new ArgumentException("Phone number must contain only numbers (digits 0-9).");
        }

        // Securely hash/encrypt password using BCrypt with workFactor 12 and enhanced salt
        var passwordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(request.Password, workFactor: 12);

        // Disallow self-registering as Admin
        var assignedRole = string.IsNullOrWhiteSpace(request.Role) ? "Customer" : request.Role.Trim();
        if (assignedRole.Equals("Admin", StringComparison.OrdinalIgnoreCase))
        {
            assignedRole = "Customer";
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Email = normalizedEmail,
            PasswordHash = passwordHash,
            Role = assignedRole,
            Address = request.Address?.Trim(),
            Nationality = request.Nationality?.Trim(),
            PhoneNumber = request.PhoneNumber?.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        var (token, expiresAt) = _tokenService.GenerateToken(user);

        return new AuthResponseDto
        {
            Token = token,
            TokenType = "Bearer",
            ExpiresAt = expiresAt,
            User = MapToUserResponseDto(user)
        };
    }

    public async Task<AuthResponseDto> LoginAsync(LoginRequestDto request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var user = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

        if (user == null)
        {
            throw new UnauthorizedAccessException("Account not registered. Please sign up or register first before signing in.");
        }

        var isPasswordValid = BCrypt.Net.BCrypt.EnhancedVerify(request.Password, user.PasswordHash) 
             || BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);

        if (!isPasswordValid)
        {
            throw new UnauthorizedAccessException("Incorrect password. Please verify your credentials and try again.");
        }

        var (token, expiresAt) = _tokenService.GenerateToken(user);

        return new AuthResponseDto
        {
            Token = token,
            TokenType = "Bearer",
            ExpiresAt = expiresAt,
            User = MapToUserResponseDto(user)
        };
    }

    public async Task<UserResponseDto?> GetUserByIdAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        return user == null ? null : MapToUserResponseDto(user);
    }

    public async Task<UserResponseDto?> UpdateProfileAsync(Guid userId, UpdateProfileDto request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return null;
        }

        if (request.FirstName != null)
        {
            user.FirstName = request.FirstName.Trim();
        }

        if (request.LastName != null)
        {
            user.LastName = request.LastName.Trim();
        }

        if (request.Address != null)
        {
            user.Address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();
        }

        if (request.Nationality != null)
        {
            user.Nationality = string.IsNullOrWhiteSpace(request.Nationality) ? null : request.Nationality.Trim();
        }

        if (request.PhoneNumber != null)
        {
            user.PhoneNumber = string.IsNullOrWhiteSpace(request.PhoneNumber) ? null : request.PhoneNumber.Trim();
        }

        await _context.SaveChangesAsync();
        return MapToUserResponseDto(user);
    }

    public async Task<IEnumerable<UserResponseDto>> GetAllUsersAsync(string? search = null)
    {
        var query = _context.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(u =>
                u.FirstName.ToLower().Contains(term) ||
                u.LastName.ToLower().Contains(term) ||
                u.Email.ToLower().Contains(term) ||
                (u.Nationality != null && u.Nationality.ToLower().Contains(term)));
        }

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync();

        return users.Select(MapToUserResponseDto);
    }

    public async Task<string> ForgotPasswordAsync(string email)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        if (user == null)
        {
            throw new ArgumentException("No account found with this email address.");
        }

        // Generate 6-digit random code
        var resetCode = Random.Shared.Next(100000, 999999).ToString();
        var expiry = DateTime.UtcNow.AddMinutes(15);
        
        user.ResetCode = resetCode;
        user.ResetCodeExpiry = expiry;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Generated password reset code for {Email}: {Code}", normalizedEmail, resetCode);
        
        var emailBody = $"<p>Your password reset code is: <strong>{resetCode}</strong></p><p>This code will expire in 15 minutes.</p>";
        await _emailService.SendEmailAsync(normalizedEmail, "Password Reset Code", emailBody);

        return resetCode;
    }

    public async Task<bool> VerifyOtpAsync(VerifyOtpRequestDto request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        
        if (user == null)
        {
            throw new ArgumentException("User account not found.");
        }

        if (string.IsNullOrEmpty(user.ResetCode) || user.ResetCodeExpiry == null)
        {
            throw new ArgumentException("No reset code was requested for this email address.");
        }

        if (DateTime.UtcNow > user.ResetCodeExpiry.Value)
        {
            user.ResetCode = null;
            user.ResetCodeExpiry = null;
            await _context.SaveChangesAsync();
            throw new ArgumentException("Reset code has expired. Please request a new code.");
        }

        if (user.ResetCode != request.ResetCode.Trim())
        {
            throw new ArgumentException("Invalid reset code. Please check the code and try again.");
        }

        return true;
    }

    public async Task<bool> ResetPasswordAsync(ResetPasswordRequestDto request)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        
        if (user == null)
        {
            throw new ArgumentException("User account not found.");
        }

        if (string.IsNullOrEmpty(user.ResetCode) || user.ResetCodeExpiry == null)
        {
            throw new ArgumentException("No reset code was requested for this email address.");
        }

        if (DateTime.UtcNow > user.ResetCodeExpiry.Value)
        {
            user.ResetCode = null;
            user.ResetCodeExpiry = null;
            await _context.SaveChangesAsync();
            throw new ArgumentException("Reset code has expired. Please request a new code.");
        }

        if (user.ResetCode != request.ResetCode.Trim())
        {
            throw new ArgumentException("Invalid reset code. Please check the code and try again.");
        }

        // Hash new password using BCrypt with workFactor 12
        user.PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(request.NewPassword, workFactor: 12);
        
        // Invalidate OTP
        user.ResetCode = null;
        user.ResetCodeExpiry = null;
        
        await _context.SaveChangesAsync();

        _logger.LogInformation("Password successfully reset for {Email}", normalizedEmail);

        return true;
    }

    private static UserResponseDto MapToUserResponseDto(User user)
    {
        return new UserResponseDto
        {
            Id = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Email = user.Email,
            Role = user.Role,
            Address = user.Address,
            Nationality = user.Nationality,
            PhoneNumber = user.PhoneNumber,
            IsSellerApproved = user.IsSellerApproved,
            CreatedAt = user.CreatedAt
        };
    }
}
