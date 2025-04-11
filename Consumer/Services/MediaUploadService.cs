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
                }
                catch (Exception e) {
                    Console.WriteLine($"Error checking codec {c}: {e.Message}");
                    continue;
                }
            }
        }

        private static ProcessStartInfo ffmpegInfo(string arguments) {
            return new ProcessStartInfo {
                FileName = Exe,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        public static Process New(string inputFilePath, string outputFilePath, int method) {
            if (!codecSet) { TryEncoders(); }
            ProcessStartInfo processStartInfo;
            switch (method) {
                case 0: // thumbnail
                    processStartInfo = ffmpegInfo($"-i \"{inputFilePath}\" -vf \"thumbnail\" -frames:v 1 \"{outputFilePath}\"");
                    break;
                case 1: // 10s preview
                    processStartInfo = ffmpegInfo($"-i \"{inputFilePath}\" -t 10 -c copy \"{outputFilePath}\"");
                    break;
                case 2: // compression
                    processStartInfo = ffmpegInfo($"-i \"{inputFilePath}\" -c:v {codec} -preset fast \"{outputFilePath}\"");
                    break;
                default: // default to thumbnail
                    processStartInfo = ffmpegInfo($"-i \"{inputFilePath}\" -vf \"thumbnail\" -frames:v 1 \"{outputFilePath}\"");
                    break;
            }
            return new Process {
                StartInfo = processStartInfo
            }; ;
        }
    }

    public class FileTable {
        private static FileTable instance;
        private static readonly object lockObject = new object();
        private Dictionary<string, string> fileTable = new Dictionary<string, string>();
        private static string _fileTablePath = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos\\fileTable.txt");
        public static FileTable Instance {
            get {
                lock (lockObject) {
                    if (instance == null) {
                        instance = new FileTable();
                    }
                }
                return instance;
            }
        }
        public void Add(string hash, string fileName) {
            lock (lockObject) {
                fileTable[hash] = fileName;
                SaveTable();
            }
        }
        public string Get(string hash) {
            lock (lockObject) {
                return fileTable.TryGetValue(hash, out var value) ? value : null;
            }
        }
        public void SaveTable() {
            lock (lockObject) {
                using (var writer = new StreamWriter(_fileTablePath)) {
                    foreach (var kvp in fileTable) {
                        writer.WriteLine($"{kvp.Key}:{kvp.Value}");
                    }
                }
            }
        }

        public void LoadTable() {
            lock (lockObject) {
                if (!File.Exists(_fileTablePath)) return;
                using (var reader = new StreamReader(_fileTablePath)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        var parts = line.Split(':');
                        if (parts.Length == 2) {
                            fileTable[parts[0]] = parts[1];
                        }
                    }
                }
            }
        }
    }

    public class MediaUploadService : MediaUpload.MediaUploadBase {
        private readonly string _uploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos");
        private readonly string _tempFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos\\Temp");
        private readonly string _previewFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos\\Previews");
        private BlockingCollection<VideoEntry> videoQueue;
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
            InitializeFiles();
            FileTable.Instance.LoadTable();
        }

        public bool Configure(int maxThreads, int maxQueue) {
            if (configSet) {
                Console.WriteLine("Service already configured. Please restart the service to reconfigure.");
                return false;
            }
            _maxConcurrentThreads = maxThreads;
            this.maxQueueLength = maxQueue;
            videoQueue = new BlockingCollection<VideoEntry>(maxQueueLength);
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
                        if (secondaryQueue.Count + videoQueue.Count >= maxQueueLength) {
                            Console.WriteLine("Queue is full. Rejecting upload.");
                            return new UploadStatus { Success = false, Message = "Queue is full." };
                        }
                        if (!secondaryQueue.TryAdd(chunk.FileName, true)) {
                            Console.WriteLine($"Failed to add video {chunk.FileName} to the secondary queue.");
                            return new UploadStatus { Success = false, Message = "Failed to add video to the secondary queue." };
                        }
                    }
                    videoEntry.VideoId = Guid.NewGuid().ToString();
                    FileTable.Instance.Add(videoEntry.VideoId, chunk.FileName);

                    //videoEntry.VideoId = Path.GetFileName(GetUniqueFileName(Path.Combine(_tempFolder, chunk.FileName)));
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
            secondaryQueue.Remove(videoEntry.VideoId, out _);
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
                Console.WriteLine($"Compressing video \"{filePath}\" to \"{compressedFilePath}\"with ffmpeg");
                var ffmpegProcess = FFmpeg.New(filePath, compressedFilePath, 2);
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

            Console.WriteLine($"Generating thumbnail of \"{filePath}\" with ffmpeg");
            var generatedThumbnail = FFmpeg.New(filePath, previewThumbnail, 0);
            Console.WriteLine($"Generating 10 second video preview \"{filePath}\" with ffmpeg");
            var generatedPreview = FFmpeg.New(filePath, previewVideo, 1);

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
        public Dictionary<string, string> GetVideoPreviews() {
            var previewFolder = Path.Combine(_uploadFolder, "Previews");
            if (!Directory.Exists(previewFolder)) {
                return new Dictionary<string, string>();
            }

            var returnDictionary = new Dictionary<string, string>();
            foreach (var file in Directory.GetFiles(previewFolder)) {
                if (Path.GetExtension(file).Equals(".png")) {
                    returnDictionary.Add(Path.GetFileName(file), FileTable.Instance.Get(Path.GetFileNameWithoutExtension(file)));
                }
            }

            return returnDictionary;
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