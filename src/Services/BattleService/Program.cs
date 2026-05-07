using BattleService.Services;
using FOBackend.Core.FrameSync;
using FOBackend.Transport.Kcp;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();

// gRPC (internal API)
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

// gRPC clients for other services
builder.Services.AddGrpcClient<global::Auth.AuthService.AuthServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration.GetValue<string>("Services:AuthUrl") ?? "http://localhost:8081");
});
builder.Services.AddGrpcClient<global::State.StateService.StateServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration.GetValue<string>("Services:StateUrl") ?? "http://localhost:8084");
});

// KCP Transport
builder.Services.Configure<KcpConfig>(builder.Configuration.GetSection("Kcp"));
builder.Services.AddSingleton<IKcpServerService, KcpServerService>();

// Battle Service
builder.Services.AddSingleton<IBattleRoomManager, BattleRoomManager>();
builder.Services.AddSingleton<IFrameUploader, GrpcFrameUploader>();
builder.Services.AddHostedService<KcpHostedService>();

var app = builder.Build();

app.MapGrpcService<BattleGrpcService>();
app.MapGrpcReflectionService();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "battle" }));

app.Run();
