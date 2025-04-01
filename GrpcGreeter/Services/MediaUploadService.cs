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
        private readonly BlockingCollection<VideoEntry> _videoQueue;
        private readonly ConcurrentDictionary<string, VideoEntry> _processingVideos = new(); // Track videos being processed
        private readonly Thread[] _workerThreads;
        private readonly int _maxConcurrentThreads;
        private readonly object _syncLock = new(); // Lock object for synchronization

        public MediaUploadService(int maxConcurrentThreads, int maxQueueLength) {
            _maxConcurrentThreads = maxConcurrentThreads;
            _videoQueue = new BlockingCollection<VideoEntry>(maxQueueLength);

            if (!Directory.Exists(_uploadFolder)) {
                Directory.CreateDirectory(_uploadFolder);
            }

            _workerThreads = new Thread[_maxConcurrentThreads];
            for (int i = 0; i < _maxConcurrentThreads; i++) {
                _workerThreads[i] = new Thread(ProcessQueue) {
                    IsBackground = true
                };
                _workerThreads[i].Start();
            }
            // write _processingVideos length to console
            Console.WriteLine($"Processing videos: {_processingVideos.Count}");
        }

        public override async Task<UploadStatus> UploadMedia(IAsyncStreamReader<VideoChunk> requestStream, ServerCallContext context)
        {
            if (requestStream == null)
            {
                return new UploadStatus { Success = false, Message = "Invalid request stream." };
            }

            VideoEntry videoEntry = new VideoEntry
            {
                VideoId = string.Empty,
                TotalExpectedChunks = 0,
                ReceivedChunks = 0
            };

            try
            {
                // Synchronize access to the queue and processingVideos
                lock (_syncLock)
                {
                    Console.WriteLine($"Queue length: {_videoQueue.Count}, Processing videos: {_processingVideos.Count}");
                    if (_videoQueue.Count + _processingVideos.Count >= _videoQueue.BoundedCapacity)
                    {
                        Console.WriteLine("Q full. Rejecting video upload.");
                        // write videoqueue length to console and processingvideos length to console
                        Console.WriteLine($"Queue length: {_videoQueue.Count}, Processing videos: {_processingVideos.Count}");
                        return new UploadStatus { Success = false, Message = "Server is busy. Please try again later." };
                    }

                    // Add to processingVideos to count it as part of the queue
                    _processingVideos.TryAdd(videoEntry.VideoId, videoEntry);
                }
                int ctr = 0;
                while (await requestStream.MoveNext())
                {
                    var chunk = requestStream.Current;
                    if (ctr == 0) {
                        Console.WriteLine($"Received first chunk for video {chunk.FileName}");
                        ctr++;
                    }
                    videoEntry.VideoId = chunk.FileName;
                    videoEntry.Chunks.Add(chunk.Data.ToByteArray());
                    videoEntry.TotalExpectedChunks = chunk.TotalChunks;
                    videoEntry.ReceivedChunks++;
                }

                if (!videoEntry.IsComplete)
                {
                    Console.WriteLine($"Video {videoEntry.VideoId} is incomplete. Rejecting upload.");
                    _processingVideos.TryRemove(videoEntry.VideoId, out _);
                    return new UploadStatus { Success = false, Message = "Video upload incomplete." };
                }

                lock (_syncLock)
                {
                    if (!_videoQueue.TryAdd(videoEntry, TimeSpan.FromMilliseconds(500)))
                    {
                        Console.WriteLine($"Failed to add video {videoEntry.VideoId} to the queue.");
                        _processingVideos.TryRemove(videoEntry.VideoId, out _);
                        return new UploadStatus { Success = false, Message = "Failed to add video to the queue." };
                    }
                    _processingVideos.TryRemove(videoEntry.VideoId, out _);
                }

                Console.WriteLine($"Video {videoEntry.VideoId} added to the queue.");
                return new UploadStatus { Success = true, Message = "Upload completed." };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading video {videoEntry.VideoId}: {ex.Message}");
                _processingVideos.TryRemove(videoEntry.VideoId, out _);
                return new UploadStatus { Success = false, Message = "Error uploading video." };
            }
        }

        private void ProcessQueue() {
            foreach (var videoEntry in _videoQueue.GetConsumingEnumerable()) {
                try {
                    // Process the video entry
                    // write current queue length to console and max queue length
                    Console.WriteLine($"Processing video: {videoEntry.VideoId}, Current queue length: {_videoQueue.Count}, Max queue length: {_videoQueue.BoundedCapacity}");
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

                // Optional: Generate a preview
                // PreviewVideo(filePath);
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
            _videoQueue.CompleteAdding(); // Signal that no more items will be added to the queue

            foreach (var thread in _workerThreads)
            {
                thread.Join(); // Wait for all worker threads to finish processing
            }

            Console.WriteLine("MediaUploadService shutdown complete.");
        }
    }

    
}