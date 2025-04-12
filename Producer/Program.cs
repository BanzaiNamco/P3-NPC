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
    private readonly List<string> _directories;

    public VideoUploader(string serverAddress, string directory, int threadCount, List<string> directories)
    {
        var channel = GrpcChannel.ForAddress(serverAddress);
        _client = new MediaUpload.MediaUpload.MediaUploadClient(channel);
        _directory = directory;
        _threadCount = threadCount;
        _directories = directories;
    }

    public async Task Start() {
        var tasks = new List<Task>();
        var directoryQueue = new ConcurrentQueue<string>(_directories);
        for (int i = 0; i < _threadCount; i++) {
            tasks.Add(Task.Run(async () => {
                int? id = Task.CurrentId;
                while (directoryQueue.TryDequeue(out var directory)) {
                    Console.WriteLine($"[Thread {id}]:\tProcessing directory: {directory}");
                    await StartUploadAsync(id, directory);
                }
            }));
        }
        await Task.WhenAll(tasks);
        Console.WriteLine("[Thread 0]:\tAll threads finished.");
    }

    public async Task StartUploadAsync(int? threadId, string directory)
    {
        var videoFiles = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories);
        var videoQueue = new ConcurrentQueue<string>(videoFiles);

        while (videoQueue.TryDequeue(out var filePath))
        {
            if (Path.GetExtension(filePath) != ".mp4" && Path.GetExtension(filePath) != ".mkv") {
                Console.WriteLine($"[Thread {threadId}]:\tSkipping non-video file: {filePath}");
                continue;
            }
            Console.WriteLine($"[Thread {threadId}]:\tUploading \"{filePath}\"");
            string hash = await Hashify(filePath);
            var hashResponse = await _client.CheckDuplicateAsync(new HashRequest { Hash = hash });
            if (hashResponse.Success) {
                await UploadVideoWithRetryAsync(threadId, filePath, hash);
            }
            else {
                Console.WriteLine($"[Thread {threadId}]:\t{hashResponse.Message}");
            }
        }

        Console.WriteLine($"[Thread {threadId}]:\tCompleted all uploads in \"{directory}\".");
    }

    private async Task<string> Hashify(string filePath) {
        var sha256 = System.Security.Cryptography.SHA256.Create();
        var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        var hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        stream.Close();
        return hash;
    }

    private async Task<bool> UploadVideoWithRetryAsync(int? threadId, string filePath, string hash)
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
                    var chunk = new VideoChunk {
                        FileName = Path.GetFileName(filePath),
                        Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
                        TotalChunks = (uint)Math.Ceiling((double)fileStream.Length / buffer.Length),
                        Hash = hash
                    };

                    await call.RequestStream.WriteAsync(chunk);
                }

                await call.RequestStream.CompleteAsync();
                var response = await call.ResponseAsync;

                if (response.Success)
                {
                    Console.WriteLine($"[Thread {threadId}]:\tUpload successful for {filePath}: {response.Message}");
                    return true; // Upload succeeded
                }
                else
                {
                    Console.WriteLine($"[Thread {threadId}]:\tServer rejected {filePath}: {response.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Thread {threadId}]:\tError uploading {filePath} {ex.Message}");
            }
        }

        return false; // Upload failed after retries
    }
}

public class Program
{
    private static List<string> directories = new List<string>();
    private static void Initialize() {
        var parentDirectory = "../../../Videos";
        var maxDirectories = 8;
        if (!Directory.Exists(parentDirectory)) {
            Directory.CreateDirectory(parentDirectory);
        }

        for (int i = 1; i <= maxDirectories; i++) {
            directories.Add($"{parentDirectory}/Thread {i}/");
        }

        foreach (var directory in directories) {
            if (!Directory.Exists(directory)) {
                Directory.CreateDirectory(directory);
            }
        }
    }
    public static async Task Main(string[] args)
    {
        Initialize();
        Console.Write("Enter the number of threads to use for uploading: ");
        int threadCount;
        while (!int.TryParse(Console.ReadLine(), out threadCount) || threadCount < 1 || threadCount > 8)
        {
            Console.WriteLine("Invalid input. Please enter an integer from 1 to 8.\n");
            Console.Write("Enter the number of threads to use for uploading: ");
        }

        var directory = "../../../Videos";
        var serverAddress = "https://localhost:7280";

        var uploader = new VideoUploader(serverAddress, directory, threadCount, directories);
        await uploader.Start();
    }
}
