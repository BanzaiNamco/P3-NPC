using Grpc.Net.Client;
using MediaUpload;

//// The port number must match the port of the gRPC server.
using var channel = GrpcChannel.ForAddress("https://localhost:7280");
var client = new MediaUpload.MediaUpload.MediaUploadClient(channel);

// Ask the user how many threads to use for uploading
int threadCount = -1;
while (threadCount < 1)
{
    Console.Write("Enter the number of threads to use for uploading: ");
    if (!int.TryParse(Console.ReadLine(), out threadCount) || threadCount < 1)
    {
        Console.WriteLine("Invalid input. Please enter a positive integer.");
    }
}

var directory = "../../../Videos";

// Get all video files in the directory
var videoFiles = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);

// Create and start threads
var tasks = new List<Task>();
foreach (var filePath in videoFiles)
{
    tasks.Add(Task.Run(async () =>
    {
        Console.WriteLine($"Uploading: {filePath}");
        await UploadVideoAsync(client, filePath);
    }));
}

// Wait for all threads to complete
await Task.WhenAll(tasks);

Console.WriteLine("All uploads completed.");

// Method to upload a single video file
static async Task UploadVideoAsync(MediaUpload.MediaUpload.MediaUploadClient client, string filePath)
{
    using var fileStream = File.OpenRead(filePath);
    using var call = client.UploadMedia();

    var buffer = new byte[64 * 1024]; // 64 KB buffer
    int bytesRead;
    while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
    {
        var chunk = new VideoChunk
        {
            FileName = Path.GetFileName(filePath),
            Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead)
        };

        await call.RequestStream.WriteAsync(chunk);
    }

    await call.RequestStream.CompleteAsync();
    var response = await call.ResponseAsync;

    Console.WriteLine($"Upload status for {filePath}: {response.Message}");
}