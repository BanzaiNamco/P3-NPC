using GrpcGreeter.Services;
using MediaUpload;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();

var app = builder.Build();

// Configure the HTTP request pipeline.
// Map the GreeterService (existing service)
app.MapGrpcService<GreeterService>();

// Map the MediaUploadService (new service)
app.MapGrpcService<MediaUploadService>();

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();
