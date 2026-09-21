using Arboveya.Api.Models;

namespace Arboveya.Api.Services;

public interface ITokenService
{
    (string Token, DateTime ExpiresAt) GenerateToken(User user);
}
