using StateService.Services;
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

// gRPC client for Auth Service
builder.Services.AddGrpcClient<global::Auth.AuthService.AuthServiceClient>(options =>
{
    options.Address = new Uri(builder.Configuration.GetValue<string>("Services:AuthUrl") ?? "http://localhost:8081");
});

// Database
var dbConnectionString = builder.Configuration.GetConnectionString("Database")
    ?? "Data Source=data/state.db";
builder.Services.AddSingleton<IMatchHistoryRepository>(_ => new SqliteMatchHistoryRepository(dbConnectionString));

// Object Storage (MinIO for dev, S3 for prod)
var minioEndpoint = builder.Configuration.GetValue<string>("ObjectStorage:Endpoint");
if (!string.IsNullOrEmpty(minioEndpoint))
{
    builder.Services.AddSingleton<IObjectStorage>(sp => new MinioObjectStorage(
        minioEndpoint,
        builder.Configuration.GetValue<string>("ObjectStorage:AccessKey") ?? "",
        builder.Configuration.GetValue<string>("ObjectStorage:SecretKey") ?? "",
        builder.Configuration.GetValue<string>("ObjectStorage:Bucket") ?? "fobackend-state",
        sp.GetRequiredService<ILogger<MinioObjectStorage>>()));
}
else
{
    builder.Services.AddSingleton<IObjectStorage, LocalFileObjectStorage>();
}

// State Service
builder.Services.AddSingleton<IFrameStorageService, FrameStorageService>();
builder.Services.AddSingleton<IStateService, StateServiceImpl>();

var app = builder.Build();

app.MapGrpcService<StateGrpcService>();
app.MapGrpcReflectionService();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "state" }));

app.Run();
