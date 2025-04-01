using GrpcGreeter.Services;
using MediaUpload;

var builder = WebApplication.CreateBuilder(args);

// Ask the user for the number of concurrent threads
Console.Write("Enter the maximum number of concurrent threads for processing: ");
int maxConcurrentThreads;
while (!int.TryParse(Console.ReadLine(), out maxConcurrentThreads) || maxConcurrentThreads < 1)
{
    Console.WriteLine("Invalid input. Please enter a positive integer.");
}

// Ask the user for the maximum queue length
Console.Write("Enter the maximum queue length: ");
int maxQueueLength;
while (!int.TryParse(Console.ReadLine(), out maxQueueLength) || maxQueueLength < 1)
{
    Console.WriteLine("Invalid input. Please enter a positive integer.");
}

// Add services to the container.
builder.Services.AddGrpc();

// Register MediaUploadService with the user-defined number of threads and queue length
builder.Services.AddSingleton(new MediaUploadService(maxConcurrentThreads, maxQueueLength));

var app = builder.Build();

// Configure the HTTP request pipeline.
// Map the GreeterService (existing service)
app.MapGrpcService<GreeterService>();

// Map the MediaUploadService (new service)
app.MapGrpcService<MediaUploadService>();

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

app.Run();
