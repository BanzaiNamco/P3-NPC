using Grpc.Net.Client;
using MediaUpload;
using System.Collections.Concurrent;

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

// Use a thread-safe queue to manage video files
var videoQueue = new ConcurrentQueue<string>(videoFiles);

// Create and start threads
var tasks = new List<Task>();
for (int i = 0; i < threadCount; i++)
{
    tasks.Add(Task.Run(async () =>
    {
        while (videoQueue.TryDequeue(out var filePath))
        {
            Console.WriteLine($"Thread {Task.CurrentId} uploading: {filePath}");
            var success = await UploadVideoWithRetryAsync(client, filePath);
        }
    }));
}

// Wait for all threads to complete
await Task.WhenAll(tasks);

Console.WriteLine("All uploads completed.");

// Method to upload a single video file with retry logic
static async Task<bool> UploadVideoWithRetryAsync(MediaUpload.MediaUpload.MediaUploadClient client, string filePath)
{
    const int maxRetries = 1; // Maximum number of retries
    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
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
                    Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
                    TotalChunks = (uint)Math.Ceiling((double)fileStream.Length / buffer.Length)
                };

                await call.RequestStream.WriteAsync(chunk);
            }

            await call.RequestStream.CompleteAsync();
            var response = await call.ResponseAsync;

            if (response.Success)
            {
                Console.WriteLine($"Upload successful for {filePath}: {response.Message}");
                return true; // Upload succeeded
            }
            else
            {
                Console.WriteLine($"Server rejected {filePath}: {response.Message}");
            }
        }
        catch (Exception ex)
        {
            // write to console, message, error, and thread id
            Console.WriteLine($"Error uploading {filePath} {ex.Message}");
        }

        // Wait before retrying
        // await Task.Delay(1000 * attempt); // Exponential backoff
    }

    return false; // Upload failed after retries
}