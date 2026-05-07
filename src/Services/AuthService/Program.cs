using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuthService.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();

// JWT Configuration
var jwtSecret = builder.Configuration.GetValue<string>("Jwt:Secret") 
    ?? throw new InvalidOperationException("JWT Secret not configured");
var jwtIssuer = builder.Configuration.GetValue<string>("Jwt:Issuer") ?? "fobackend-auth";
var jwtAudience = builder.Configuration.GetValue<string>("Jwt:Audience") ?? "fobackend-services";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(5)
        };
    });

builder.Services.AddAuthorization();

// gRPC
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

// Database
var dbConnectionString = builder.Configuration.GetConnectionString("Database") 
    ?? "Data Source=data/auth.db";
builder.Services.AddSingleton<IPlayerRepository>(_ => new SqlitePlayerRepository(dbConnectionString));

// Redis (for token blacklist)
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConnection))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnection));
}

// Auth Service
builder.Services.AddSingleton<JwtTokenService>(_ => new JwtTokenService(jwtSecret, jwtIssuer, jwtAudience));
builder.Services.AddSingleton<IAuthService, AuthServiceImpl>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<AuthGrpcService>();
app.MapGrpcReflectionService();

// HTTP health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "auth" }));

app.Run();
