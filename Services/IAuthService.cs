using Arboveya.Api.DTOs;

namespace Arboveya.Api.Services;

public interface IAuthService
{
    Task<AuthResponseDto> RegisterAsync(RegisterRequestDto request);
    Task<AuthResponseDto> LoginAsync(LoginRequestDto request);
    Task<UserResponseDto?> GetUserByIdAsync(Guid userId);
    Task<UserResponseDto?> UpdateProfileAsync(Guid userId, UpdateProfileDto request);
    Task<IEnumerable<UserResponseDto>> GetAllUsersAsync(string? search = null);
    Task<string> ForgotPasswordAsync(string email);
    Task<bool> VerifyOtpAsync(VerifyOtpRequestDto request);
    Task<bool> ResetPasswordAsync(ResetPasswordRequestDto request);
}
