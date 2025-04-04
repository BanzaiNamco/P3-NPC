using Grpc.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace MediaUpload
{
    public class VideoEntry {
        public string VideoId { get; set; }
        public List<byte[]> Chunks { get; set; } = new List<byte[]>();
        public uint TotalExpectedChunks { get; set; }
        public uint ReceivedChunks { get; set; }

        public bool IsComplete => ReceivedChunks >= TotalExpectedChunks;
    }

    public class MediaUploadService : MediaUpload.MediaUploadBase {
        private readonly string _uploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos");
        private readonly BlockingCollection<VideoEntry> videoQueue;
        private readonly ConcurrentDictionary<string, bool> secondaryQueue = new();
        private int maxQueueLength = 10;
        private Thread[] _workerThreads;
        private int _maxConcurrentThreads;
        private readonly object objectLock = new object();

        public MediaUploadService() {
            videoQueue = new BlockingCollection<VideoEntry>(maxQueueLength);

            if (!Directory.Exists(_uploadFolder)) {
                Directory.CreateDirectory(_uploadFolder);
            }
        }

        public void Configure(int maxThreads, int maxQueue)
        {
            _maxConcurrentThreads = maxThreads;
            this.maxQueueLength = maxQueue;

            _workerThreads = new Thread[_maxConcurrentThreads];
            for (int i = 0; i < _maxConcurrentThreads; i++)
            {
                _workerThreads[i] = new Thread(ProcessQueue)
                {
                    IsBackground = true
                };
                _workerThreads[i].Start();
            }

            Console.WriteLine($"Configured MediaUploadService with {maxThreads} threads and a queue length of {maxQueue}.");
        }

        public override async Task<UploadStatus> UploadMedia(IAsyncStreamReader<VideoChunk> requestStream, ServerCallContext context)
        {
            if (requestStream == null)
            {
                return new UploadStatus { Success = false, Message = "Invalid request stream." };
            }

            VideoEntry videoEntry = new()
            {
                VideoId = string.Empty,
                TotalExpectedChunks = 0,
                ReceivedChunks = 0
            };

            await foreach (var chunk in requestStream.ReadAllAsync())
            {
                // if it is the first chunk, initialize the video name
                if (videoEntry.VideoId == string.Empty)
                {
                    lock (objectLock)
                    {
                        if (secondaryQueue.Count >= maxQueueLength)
                        {
                            Console.WriteLine("Secondary queue is full. Rejecting upload.");
                            return new UploadStatus { Success = false, Message = "Queue is full." };
                        }
                        if (!secondaryQueue.TryAdd(chunk.FileName, true))
                        {
                            Console.WriteLine($"Failed to add video {chunk.FileName} to the secondary queue.");
                            return new UploadStatus { Success = false, Message = "Failed to add video to the secondary queue." };
                        }
                    }
                    
                    videoEntry.VideoId = chunk.FileName;
                    videoEntry.TotalExpectedChunks = chunk.TotalChunks;
                }
                videoEntry.Chunks.Add(chunk.Data.ToByteArray());
                videoEntry.ReceivedChunks++;
            }

            if (!videoEntry.IsComplete)
            {
                Console.WriteLine($"Video {videoEntry.VideoId} is incomplete. Rejecting upload.");
                // Remove from secondary queue if not complete
                secondaryQueue.TryRemove(videoEntry.VideoId, out _);
                return new UploadStatus { Success = false, Message = "Video upload incomplete." };
            }

            if (!videoQueue.TryAdd(videoEntry, TimeSpan.FromSeconds(1)))
            {
                Console.WriteLine($"Failed to add video {videoEntry.VideoId} to the queue.");
                // Remove from secondary queue if failed to add to the main queue
                secondaryQueue.Remove(videoEntry.VideoId, out _);
                return new UploadStatus { Success = false, Message = "Failed to add video to the queue." };
            }

            Console.WriteLine($"Video {videoEntry.VideoId} added to the queue.");
            return new UploadStatus { Success = true, Message = "Upload completed." };
        }

        private void ProcessQueue() {
            foreach (var videoEntry in videoQueue.GetConsumingEnumerable()) {
                try {
                    secondaryQueue.Remove(videoEntry.VideoId, out _);
                    ProcessVideo(videoEntry);
                }
                catch (Exception ex) {
                    Console.WriteLine($"Error processing video: {ex.Message}");
                }
            }
        }

        private void ProcessVideo(VideoEntry videoEntry) {
            try {
                // Save the video to disk
                var filePath = Path.Combine(_uploadFolder, videoEntry.VideoId);
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);

                foreach (var chunk in videoEntry.Chunks) {
                    fileStream.Write(chunk, 0, chunk.Length);
                }

                Console.WriteLine($"Video saved: {filePath}");
            }
            catch (Exception ex) {
                Console.WriteLine($"Error processing video {videoEntry.VideoId}: {ex.Message}");
            }
        }

        private void PreviewVideo(string filePath) {
            var previewPath = Path.Combine(_uploadFolder, "preview_" + Path.GetFileName(filePath));
            var ffmpegArgs = $"-i \"{filePath}\" -t 10 -c copy \"{previewPath}\"";

            var process = new Process {
                StartInfo = new ProcessStartInfo {
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


        public void Shutdown() {
            Console.WriteLine("Shutting down MediaUploadService...");
            videoQueue.CompleteAdding(); // Signal that no more items will be added to the queue

            foreach (var thread in _workerThreads)
            {
                thread.Join(); // Wait for all worker threads to finish processing
            }

            Console.WriteLine("MediaUploadService shutdown complete.");
        }
    }

    
}