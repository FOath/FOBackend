using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Services;

/// <summary>
/// JWT Token 服务
/// </summary>
public class JwtTokenService
{
    private readonly string _secret;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly SymmetricSecurityKey _signingKey;

    public JwtTokenService(string secret, string issuer, string audience)
    {
        _secret = secret;
        _issuer = issuer;
        _audience = audience;
        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public (string Token, DateTime ExpiresAt) GenerateAccessToken(string playerId, string playerName)
    {
        var expiresAt = DateTime.UtcNow.AddHours(2);
        var tokenHandler = new JwtSecurityTokenHandler();
        
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, playerId),
                new Claim(JwtRegisteredClaimNames.Name, playerName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            }),
            Expires = expiresAt,
            Issuer = _issuer,
            Audience = _audience,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return (tokenHandler.WriteToken(token), expiresAt);
    }

    public (string Token, DateTime ExpiresAt) GenerateRefreshToken(string playerId, string deviceId)
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes);
        return (token, expiresAt);
    }

    public TokenPayload? ValidateAccessToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _signingKey,
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
            var playerId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? "";
            var playerName = principal.FindFirst(JwtRegisteredClaimNames.Name)?.Value ?? "";
            var expiry = principal.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
            
            var expiresAt = expiry != null 
                ? DateTimeOffset.FromUnixTimeSeconds(long.Parse(expiry)).UtcDateTime 
                : DateTime.UtcNow.AddHours(2);

            return new TokenPayload(playerId, playerName, expiresAt);
        }
        catch
        {
            return null;
        }
    }
}

public record TokenPayload(string PlayerId, string PlayerName, DateTime ExpiresAt);
