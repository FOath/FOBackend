using MatchmakingService.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();

// gRPC
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

// HTTP client for Auth Service
builder.Services.AddGrpcClient<global::Auth.AuthService.AuthServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration.GetValue<string>("Services:AuthUrl") ?? "http://localhost:8081");
});

// HTTP client for Battle Service
builder.Services.AddGrpcClient<global::Battle.BattleService.BattleServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration.GetValue<string>("Services:BattleUrl") ?? "http://localhost:9081");
});

// Redis
var redisConnection = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConnection))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnection));
    builder.Services.AddSingleton<IRoomManager, RedisRoomManager>();
}
else
{
    // Fallback: use in-memory room manager for development
    builder.Services.AddSingleton<IRoomManager, InMemoryRoomManager>();
}

// Services
builder.Services.AddSingleton<IBattleNodeRegistry, InMemoryBattleNodeRegistry>();
builder.Services.AddSingleton<IMatchmakingQueue, EloMatchmakingQueue>();
builder.Services.AddSingleton<IMatchmakingService, MatchmakingServiceImpl>();

var app = builder.Build();

app.MapGrpcService<MatchmakingGrpcService>();
app.MapGrpcReflectionService();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "matchmaking" }));

app.Run();
