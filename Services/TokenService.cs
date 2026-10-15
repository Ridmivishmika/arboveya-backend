using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Arboveya.Api.Extensions;
using Arboveya.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace Arboveya.Api.Services;

public class TokenService : ITokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public (string Token, DateTime ExpiresAt) GenerateToken(User user)
    {
        var secretKey = _configuration.ResolveEnv("Jwt:Key", "Jwt__Key", "JWT_KEY")
            ?? throw new InvalidOperationException("JWT Key not configured. Please set 'Jwt__Key' or 'JWT_KEY' in environment variables.");
        var issuer = _configuration.ResolveEnv("Jwt:Issuer", "Jwt__Issuer", "JWT_ISSUER") ?? "ArboveyaApi";
        var audience = _configuration.ResolveEnv("Jwt:Audience", "Jwt__Audience", "JWT_AUDIENCE") ?? "ArboveyaClient";
        var expiryMinutesStr = _configuration.ResolveEnv("Jwt:ExpiryMinutes", "Jwt__ExpiryMinutes", "JWT_EXPIRY_MINUTES");
        var expiryMinutes = int.TryParse(expiryMinutesStr, out var minutes) ? minutes : 1440; // 24h default

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var expiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.GivenName, user.FirstName),
            new(ClaimTypes.Surname, user.LastName),
            new(ClaimTypes.Name, $"{user.FirstName} {user.LastName}".Trim()),
            new(ClaimTypes.Role, user.Role),
            new("role", user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);

        return (tokenHandler.WriteToken(token), expiresAt);
    }
}
