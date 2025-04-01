using Grpc.Core;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace MediaUpload
{
    public class MediaUploadService : MediaUpload.MediaUploadBase
    {
        private readonly string _uploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos");
        private readonly ConcurrentQueue<string> _videoQueue = new(); // Server-side queue
        private readonly SemaphoreSlim _semaphore; // Limit concurrent processing (e.g., 4 threads)
        private readonly int _maxQueueLength; // Maximum queue length

        public MediaUploadService( int maxConcurrentThreads, int maxQueueLength)
        {
            _semaphore = new SemaphoreSlim(maxConcurrentThreads); // Initialize semaphore with user-defined threads
            _maxQueueLength = maxQueueLength; // Set maximum queue length

            if (!Directory.Exists(_uploadFolder))
            {
                Directory.CreateDirectory(_uploadFolder);
            }
        }

        public override async Task<UploadStatus> UploadMedia(IAsyncStreamReader<VideoChunk> requestStream, ServerCallContext context)
        {
            // Check if the queue is full
            if (_videoQueue.Count >= _maxQueueLength)
            {
                Console.WriteLine("Queue is full. Rejecting upload.");
                return new UploadStatus { Success = false, Message = "Queue is full. Try again later." };
            }

            string fileName = null;
            using (var memoryStream = new MemoryStream())
            {
                while (await requestStream.MoveNext())
                {
                    var chunk = requestStream.Current;
                    fileName ??= chunk.FileName;
                    memoryStream.Write(chunk.Data.ToByteArray());
                }

                var filePath = Path.Combine(_uploadFolder, fileName);
                await File.WriteAllBytesAsync(filePath, memoryStream.ToArray());

                // Enqueue the file for processing
                _videoQueue.Enqueue(filePath);
                Console.WriteLine($"File enqueued: {filePath}");

                // Start processing the queue
                _ = Task.Run(() => ProcessQueueAsync());
            }

            return new UploadStatus { Success = true, Message = "File uploaded and enqueued successfully." };
        }

        private async Task ProcessQueueAsync()
        {
            while (_videoQueue.TryDequeue(out var filePath))
            {
                await _semaphore.WaitAsync(); // Limit concurrent processing
                try
                {
                    Console.WriteLine($"Processing file: {filePath}");
                    PreviewVideo(filePath); // Optional: Generate a preview
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }

        private void PreviewVideo(string filePath)
        {
            var previewPath = Path.Combine(_uploadFolder, "preview_" + Path.GetFileName(filePath));
            var ffmpegArgs = $"-i \"{filePath}\" -t 10 -c copy \"{previewPath}\"";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            process.WaitForExit();
        }
    }
}