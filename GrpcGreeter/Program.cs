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
var mediaUploadService = new MediaUploadService(maxConcurrentThreads, maxQueueLength);
builder.Services.AddSingleton(mediaUploadService);

var app = builder.Build();

// Map the MediaUploadService (new service)
app.MapGrpcService<MediaUploadService>();

app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

// for shutting down the service gracefully
var lifetime = app.Lifetime;
lifetime.ApplicationStopping.Register(() =>
{
    mediaUploadService.Shutdown(); 
});

app.Run();
