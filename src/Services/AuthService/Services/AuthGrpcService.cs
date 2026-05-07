using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace AuthService.Services;

/// <summary>
/// Auth Service gRPC 实现
/// 提供玩家注册/登录、Token 验证、档案管理
/// </summary>
public class AuthGrpcService : global::Auth.AuthService.AuthServiceBase
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthGrpcService> _logger;

    public AuthGrpcService(IAuthService authService, ILogger<AuthGrpcService> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public override async Task<global::Auth.AuthenticateResponse> Authenticate(
        global::Auth.AuthenticateRequest request, ServerCallContext context)
    {
        _logger.LogInformation("Authenticate request: {PlayerName}", request.PlayerName);
        
        var result = await _authService.AuthenticateAsync(
            request.PlayerName, 
            request.Password,
            request.ClientVersion,
            request.DeviceId);
        
        return new global::Auth.AuthenticateResponse
        {
            PlayerId = result.PlayerId,
            PlayerName = result.PlayerName,
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            ExpiresAt = result.ExpiresAt,
            IsNewPlayer = result.IsNewPlayer
        };
    }

    public override async Task<global::Auth.ValidateTokenResponse> ValidateToken(
        global::Auth.ValidateTokenRequest request, ServerCallContext context)
    {
        var result = await _authService.ValidateTokenAsync(request.AccessToken);
        
        return new global::Auth.ValidateTokenResponse
        {
            Valid = result.Valid,
            PlayerId = result.PlayerId,
            PlayerName = result.PlayerName,
            ExpiresAt = result.ExpiresAt
        };
    }

    public override async Task<global::Auth.RefreshTokenResponse> RefreshToken(
        global::Auth.RefreshTokenRequest request, ServerCallContext context)
    {
        var result = await _authService.RefreshTokenAsync(request.RefreshToken);
        
        return new global::Auth.RefreshTokenResponse
        {
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            ExpiresAt = result.ExpiresAt
        };
    }

    public override async Task<global::Auth.RevokeTokenResponse> RevokeToken(
        global::Auth.RevokeTokenRequest request, ServerCallContext context)
    {
        var success = await _authService.RevokeTokenAsync(request.AccessToken);
        return new global::Auth.RevokeTokenResponse { Success = success };
    }

    public override async Task<global::Auth.GetProfileResponse> GetProfile(
        global::Auth.GetProfileRequest request, ServerCallContext context)
    {
        var profile = await _authService.GetProfileAsync(request.PlayerId);
        
        if (profile == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Player not found"));
        
        return new global::Auth.GetProfileResponse
        {
            PlayerId = profile.PlayerId,
            PlayerName = profile.PlayerName,
            TotalGames = profile.TotalGames,
            TotalWins = profile.TotalWins,
            Rating = profile.Rating,
            CreatedAt = profile.CreatedAt,
            LastLoginAt = profile.LastLoginAt
        };
    }

    public override async Task<global::Auth.UpdateProfileResponse> UpdateProfile(
        global::Auth.UpdateProfileRequest request, ServerCallContext context)
    {
        var success = await _authService.UpdateProfileAsync(request.PlayerId, request.PlayerName);
        return new global::Auth.UpdateProfileResponse { Success = success };
    }
}
