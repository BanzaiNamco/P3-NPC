using Grpc.Net.Client;
using MediaUpload;
using System.Collections.Concurrent;

public class VideoEntry
{
    public string VideoId { get; set; }
    public List<byte[]> Chunks { get; set; } = new List<byte[]>();
}

public class VideoUploader
{
    private readonly MediaUpload.MediaUpload.MediaUploadClient _client;
    private readonly string _directory;
    private readonly int _threadCount;

    public VideoUploader(string serverAddress, string directory, int threadCount)
    {
        var channel = GrpcChannel.ForAddress(serverAddress);
        _client = new MediaUpload.MediaUpload.MediaUploadClient(channel);
        _directory = directory;
        _threadCount = threadCount;
    }

    public async Task StartUploadAsync()
    {
        // Get all video files in the directory
        var videoFiles = Directory.GetFiles(_directory, "*.*", SearchOption.AllDirectories);

        // Use a thread-safe queue to manage video files
        var videoQueue = new ConcurrentQueue<string>(videoFiles);

        // Create and start threads
        var tasks = new List<Task>();
        for (int i = 0; i < _threadCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                while (videoQueue.TryDequeue(out var filePath))
                {
                    Console.WriteLine($"Thread {Task.CurrentId} uploading: {filePath}");
                    var success = await UploadVideoWithRetryAsync(filePath);
                }
            }));
        }

        // Wait for all threads to complete
        await Task.WhenAll(tasks);

        Console.WriteLine("All uploads completed.");
    }

    private async Task<bool> UploadVideoWithRetryAsync(string filePath)
    {
        const int maxRetries = 1; // Maximum number of retries
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var fileStream = File.OpenRead(filePath);
                using var call = _client.UploadMedia();

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
                Console.WriteLine($"Error uploading {filePath} {ex.Message}");
            }
        }

        return false; // Upload failed after retries
    }
}

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.Write("Enter the number of threads to use for uploading: ");
        int threadCount;
        while (!int.TryParse(Console.ReadLine(), out threadCount) || threadCount < 1)
        {
            Console.WriteLine("Invalid input. Please enter a positive integer.");
        }

        var directory = "../../../Videos";
        var serverAddress = "https://localhost:7280";

        var uploader = new VideoUploader(serverAddress, directory, threadCount);
        await uploader.StartUploadAsync();
    }
}
