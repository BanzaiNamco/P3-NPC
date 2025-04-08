using Grpc.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Numerics;
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

    public class FFmpeg {
        private static string codec = "";
        private static bool codecSet = false;
        public static string Exe = Path.Combine(Directory.GetCurrentDirectory(), "FFmpeg", "bin", "ffmpeg.exe");

        public static void TryEncoders() {
            List<string> codecList = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
            foreach (string c in codecList) {
                try {
                    var process = new Process {
                        StartInfo = new ProcessStartInfo {
                            FileName = Exe,
                            Arguments = $"-f lavfi -i nullsrc -c:v {c} -frames:v 1 -f null -", // thank you u/avsaase for this
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

                    if (process.ExitCode == 0) {
                        codec = c;
                        Console.WriteLine($"Codec Set: {codec}");
                        codecSet = true;
                        break;
                    }
                } catch (Exception e) {
                    Console.WriteLine($"Error checking codec {c}: {e.Message}");
                    continue;
                }
            }
        }

        public static Process CompressVideo(string inputFilePath, string outputFilePath) {
            if (!codecSet) { TryEncoders(); }
            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = Exe,
                    Arguments = $"-i \"{inputFilePath}\" -vcodec {codec} -preset fast \"{outputFilePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            return process;
        }

        public static Process GenerateThumbnail(string inputFilePath, string outputFilePath) {
            if (!codecSet) { TryEncoders(); }
            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = Exe,
                    Arguments = $"-i \"{inputFilePath}\" -ss 00:00:00.000 -vframes 1 -q:v 2 \"{outputFilePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            return process;
        }

        public static Process GeneratePreview(string inputFilePath, string outputFilePath) {
            if (!codecSet) { TryEncoders(); }
            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = Exe,
                    Arguments = $"-i \"{inputFilePath}\" -vcodec {codec} -t 10 -c copy \"{outputFilePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            return process;
        }
    }

    public class MediaUploadService : MediaUpload.MediaUploadBase {
        private readonly string _uploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos");
        private readonly string _tempFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos\\Temp");
        private readonly string _previewFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos\\Previews");
        private readonly BlockingCollection<VideoEntry> videoQueue;
        private readonly ConcurrentDictionary<string, bool> secondaryQueue = new();
        private int maxQueueLength = 10;
        private Thread[] _workerThreads;
        private int _maxConcurrentThreads;
        private readonly object objectLock = new object();
        private bool configSet = false;
        public event Action OnVideosChanged;
        private static List<Process> ffmpegProcesses = new List<Process>();
        private readonly SemaphoreSlim compressionSemaphore = new (3);
        public void NotifyVideosChanged() {
            OnVideosChanged?.Invoke();
        }

        private void InitializeFiles() {
            if (!Directory.Exists(_uploadFolder)) {
                Directory.CreateDirectory(_uploadFolder);
            }
            if (!Directory.Exists(_tempFolder)) {
                Directory.CreateDirectory(_tempFolder);
            }
            if (!Directory.Exists(_previewFolder)) {
                Directory.CreateDirectory(_previewFolder);
            }
        }

        public MediaUploadService() {
            videoQueue = new BlockingCollection<VideoEntry>(maxQueueLength);
            InitializeFiles();
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
            FFmpeg.TryEncoders();
            return true;
        }

        public override async Task<UploadStatus> UploadMedia(IAsyncStreamReader<VideoChunk> requestStream, ServerCallContext context) {
            InitializeFiles();
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

                var filePath = Path.Combine(_tempFolder, videoEntry.VideoId);
                {
                    using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);

                    foreach (var chunk in videoEntry.Chunks) {
                        fileStream.Write(chunk, 0, chunk.Length);
                    }
                }

                Console.WriteLine($"Video downloaded: {filePath}");

                //if (Path.GetExtension(filePath).Equals(".mkv", StringComparison.OrdinalIgnoreCase)) {
                //    Console.WriteLine($"Converting MKV to MP4 for video {videoEntry.VideoId}...");
                //    filePath = ConvertMkvToMp4(filePath);
                //    Console.WriteLine($"Conversion complete for video {videoEntry.VideoId}. New file path: {filePath}");
                //}

                Console.WriteLine($"Generating preview for video {videoEntry.VideoId}...");
                GeneratePreviews(filePath);
                Console.WriteLine($"Preview generated for video {videoEntry.VideoId}.");
                NotifyVideosChanged();
                await CompressVideo(filePath);
                File.Delete(filePath);
            }
            catch (Exception ex) {
                Console.WriteLine($"Error processing video {videoEntry.VideoId}: {ex.Message}");
            }
        }

        //private static string ConvertMkvToMp4(string inputFilePath) {
        //    var outputFilePath = GetUniqueFileName($"{Path.Combine(_uploadFolder, Path.GetFileNameWithoutExtension(inputFilePath)}");
        //    var uniqueFilePath = GetUniqueFileName(inputFilePath);
        //    var ffmpegArgs = $"-i \"{inputFilePath}\" -hwaccel cuda -hwaccel_device 0 -c:v h265_nvenc -preset slow -crf 24 \"{uniqueFilePath}\"";
        //    Console.WriteLine($"Converting MKV to MP4 with ffmpeg");
        //    try {

        //    var process = new Process {
        //        StartInfo = new ProcessStartInfo {
        //            FileName = Path.Combine(Directory.GetCurrentDirectory(), "FFmpeg", "bin", "ffmpeg.exe"),
        //            Arguments = ffmpegArgs,
        //            RedirectStandardOutput = true,
        //            RedirectStandardError = true,
        //            UseShellExecute = false,
        //            CreateNoWindow = true
        //        }
        //    };

        //    process.OutputDataReceived += (sender, args) => { };
        //    process.ErrorDataReceived += (sender, args) => { };
        //    ffmpegProcesses.Add(process);
        //    process.Start();
        //    process.BeginOutputReadLine();
        //    process.BeginErrorReadLine();
        //    process.WaitForExit();

        //    }  catch (Exception e) {
        //        Console.WriteLine(e);
        //    }

        //    //if (process.ExitCode != 0) {
        //    //    throw new Exception($"FFmpeg conversion failed for {inputFilePath}");
        //    //}

        //    File.Delete(inputFilePath);
        //    return outputFilePath;
        //}

        private static string GetUniqueFileName(string filePath) {
            if (!File.Exists(filePath)) {
                return filePath;
            }
            
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
            string fileDirectory = Path.GetDirectoryName(filePath);
            string extension = Path.GetExtension(filePath);
            int count = 1;

            string newFileName;
            string newFullPath;

            do {
                newFileName = $"{fileNameWithoutExtension} ({count}){extension}";
                newFullPath = Path.Combine(fileDirectory, newFileName);
                count++;
            } while (File.Exists(newFullPath));

            return newFullPath;
        }

        private async Task CompressVideo(string filePath) {
            await compressionSemaphore.WaitAsync();
            try {
                InitializeFiles();

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var compressedFilePath = Path.Combine(_uploadFolder, $"{fileName}.mp4");
                compressedFilePath = GetUniqueFileName(compressedFilePath);
                Console.WriteLine($"Compressing video with ffmpeg");
                var ffmpegProcess = FFmpeg.CompressVideo(filePath, compressedFilePath);
                ffmpegProcess.OutputDataReceived += (sender, args) => { };
                ffmpegProcess.ErrorDataReceived += (sender, args) => { };
                ffmpegProcesses.Add(ffmpegProcess);
                ffmpegProcess.Start();
                ffmpegProcess.BeginOutputReadLine();
                ffmpegProcess.BeginErrorReadLine();
                await ffmpegProcess.WaitForExitAsync();
            }
            finally {
                compressionSemaphore.Release();
            }

            return;
        }

        private async Task GeneratePreviews(string filePath) {
            InitializeFiles();

            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var previewVideo = Path.Combine(_previewFolder, $"{fileName}.mp4");
            var previewThumbnail = Path.Combine(_previewFolder, $"{fileName}.png");

            previewVideo = GetUniqueFileName(previewVideo);
            previewThumbnail = GetUniqueFileName(previewThumbnail);

            Console.WriteLine($"Generating thumbnail with ffmpeg");
            var generatedThumbnail = FFmpeg.GenerateThumbnail(filePath, previewThumbnail);
            Console.WriteLine($"Generating video preview with ffmpeg");
            var generatedPreview = FFmpeg.GeneratePreview(filePath, previewVideo);

            generatedThumbnail.OutputDataReceived += (sender, args) => { };
            generatedThumbnail.ErrorDataReceived += (sender, args) => { };
            generatedPreview.OutputDataReceived += (sender, args) => { };
            generatedPreview.ErrorDataReceived += (sender, args) => { };

            ffmpegProcesses.Add(generatedThumbnail);
            ffmpegProcesses.Add(generatedPreview);

            generatedThumbnail.Start();
            generatedThumbnail.BeginOutputReadLine();
            generatedThumbnail.BeginErrorReadLine();
            await generatedThumbnail.WaitForExitAsync();


            generatedPreview.Start();
            generatedPreview.BeginOutputReadLine();
            generatedPreview.BeginErrorReadLine();
            await generatedPreview.WaitForExitAsync();

            return;
        }
        public List<string> GetVideoPreviews() {
            var previewFolder = Path.Combine(_uploadFolder, "Previews");
            if (!Directory.Exists(previewFolder)) {
                return new List<string>();
            }

            return [.. Directory.GetFiles(previewFolder)
                    .Where(file => !(Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))).Select(Path.GetFileName)];
        }

        public bool isConfigSet() { return configSet; }

        public void Shutdown() {
            Console.WriteLine("Shutting down MediaUploadService...");
            videoQueue.CompleteAdding();

            foreach (var thread in _workerThreads)
            {
                thread.Join();
            }
            foreach (var process in ffmpegProcesses) {
                if (!process.HasExited) {
                    process.Kill();
                }
            }
            Console.WriteLine("MediaUploadService shutdown complete.");
        }
    }
}