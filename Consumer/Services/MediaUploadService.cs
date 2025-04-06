using Grpc.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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
        private bool configSet = false;
        public event Action OnVideosChanged;

        public void NotifyVideosChanged() {
            OnVideosChanged?.Invoke();
        }

        public MediaUploadService() {
            videoQueue = new BlockingCollection<VideoEntry>(maxQueueLength);

            if (!Directory.Exists(_uploadFolder)) {
                Directory.CreateDirectory(_uploadFolder);
            }
        }

        public bool Configure(int maxThreads, int maxQueue) {
            if (configSet) {
                Console.WriteLine("Service already configured. Please restart the service to reconfigure.");
                return false;
            }
            _maxConcurrentThreads = maxThreads;
            this.maxQueueLength = maxQueue;

            _workerThreads = new Thread[_maxConcurrentThreads];
            for (int i = 0; i < _maxConcurrentThreads; i++) {
                _workerThreads[i] = new Thread(ProcessQueue) {
                    IsBackground = true
                };
                _workerThreads[i].Start();
            }

            Console.WriteLine($"Configured MediaUploadService with {maxThreads} threads and a queue length of {maxQueue}.");
            configSet = true;
            return true;
        }

        public override async Task<UploadStatus> UploadMedia(IAsyncStreamReader<VideoChunk> requestStream, ServerCallContext context) {
            if (requestStream == null) {
                return new UploadStatus { Success = false, Message = "Invalid request stream." };
            }

            if (!configSet) {
                Console.WriteLine("Service not configured. Please configure before uploading.");
                return new UploadStatus { Success = false, Message = "Service not configured." };
            }

            VideoEntry videoEntry = new() {
                VideoId = string.Empty,
                TotalExpectedChunks = 0,
                ReceivedChunks = 0
            };

            await foreach (var chunk in requestStream.ReadAllAsync()) {
                // if it is the first chunk, initialize the video name
                if (videoEntry.VideoId == string.Empty) {
                    lock (objectLock) {
                        if (secondaryQueue.Count >= maxQueueLength) {
                            Console.WriteLine("Secondary queue is full. Rejecting upload.");
                            return new UploadStatus { Success = false, Message = "Queue is full." };
                        }
                        if (!secondaryQueue.TryAdd(chunk.FileName, true)) {
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

            if (!videoEntry.IsComplete) {
                Console.WriteLine($"Video {videoEntry.VideoId} is incomplete. Rejecting upload.");
                secondaryQueue.TryRemove(videoEntry.VideoId, out _);
                return new UploadStatus { Success = false, Message = "Video upload incomplete." };
            }

            if (!videoQueue.TryAdd(videoEntry, TimeSpan.FromSeconds(1))) {
                Console.WriteLine($"Failed to add video {videoEntry.VideoId} to the queue.");
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

        private async Task ProcessVideo(VideoEntry videoEntry) {
            try {

                var filePath = Path.Combine(_uploadFolder, videoEntry.VideoId);
                {
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);

                    foreach (var chunk in videoEntry.Chunks) {
                        fileStream.Write(chunk, 0, chunk.Length);
                    }
                }

                Console.WriteLine($"Video saved: {filePath}");

                if (Path.GetExtension(filePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) {
                    Console.WriteLine($"Converting MKV to MP4 for video {videoEntry.VideoId}...");
                    filePath = ConvertMkvToMp4(filePath);
                    Console.WriteLine($"Conversion complete for video {videoEntry.VideoId}. New file path: {filePath}");
                }

                Console.WriteLine($"Generating preview for video {videoEntry.VideoId}...");
                await PreviewVideo(filePath);
                Console.WriteLine($"Preview generated for video {videoEntry.VideoId}.");
                NotifyVideosChanged();
            }
            catch (Exception ex) {
                Console.WriteLine($"Error processing video {videoEntry.VideoId}: {ex.Message}");
            }
        }

        private static string ConvertMkvToMp4(string inputFilePath) {
            var outputFilePath = Path.ChangeExtension(inputFilePath, ".mp4");
            var ffmpegArgs = $"-i \"{inputFilePath}\" -c:v copy -c:a copy \"{outputFilePath}\"";
            Console.WriteLine($"Converting MKV to MP4 with ffmpeg");

            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = Path.Combine(Directory.GetCurrentDirectory(), "FFmpeg", "bin", "ffmpeg.exe"),
                    Arguments = ffmpegArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.OutputDataReceived += (sender, args) => { };
            process.ErrorDataReceived += (sender, args) => { };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode != 0) {
                throw new Exception($"FFmpeg conversion failed for {inputFilePath}");
            }

            File.Delete(inputFilePath);
            return outputFilePath;
        }

        private async Task PreviewVideo(string filePath) {
            var previewFolder = Path.Combine(_uploadFolder, "Previews");
            if (!Directory.Exists(previewFolder)) {
                Directory.CreateDirectory(previewFolder);
            }
            var previewPath = Path.Combine(previewFolder, "preview_" + Path.GetFileName(filePath));
            var ffmpegArgs = $"-i \"{filePath}\" -t 10 -c copy \"{previewPath}\"";
            Console.WriteLine($"Generating preview with ffmpeg");
            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = Path.Combine(Directory.GetCurrentDirectory(), "FFmpeg", "bin", "ffmpeg.exe"),
                    Arguments = ffmpegArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.OutputDataReceived += (sender, args) => { };
            process.ErrorDataReceived += (sender, args) => { };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            return;
        }
        public List<string> GetVideoPreviews() {
            var previewFolder = Path.Combine(_uploadFolder, "Previews");
            if (!Directory.Exists(previewFolder)) {
                return new List<string>();
            }

            return [.. Directory.GetFiles(previewFolder)
                    .Where(file => !Path.GetExtension(file).Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName)];
        }

        public bool isConfigSet() { return configSet; }

        public void Shutdown() {
            Console.WriteLine("Shutting down MediaUploadService...");
            videoQueue.CompleteAdding();

            foreach (var thread in _workerThreads)
            {
                thread.Join();
            }

            Console.WriteLine("MediaUploadService shutdown complete.");
        }
    }

    
}