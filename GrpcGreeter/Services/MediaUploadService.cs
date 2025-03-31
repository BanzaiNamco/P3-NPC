using Grpc.Core;
using System.Diagnostics;

namespace MediaUpload
{
    public class MediaUploadService : MediaUpload.MediaUploadBase
    {
        private readonly string _uploadFolder = Path.Combine(Directory.GetCurrentDirectory(), "UploadedVideos");

        public MediaUploadService()
        {
            if (!Directory.Exists(_uploadFolder))
            {
                Directory.CreateDirectory(_uploadFolder);
            }
        }

        public override async Task<UploadStatus> UploadMedia(IAsyncStreamReader<VideoChunk> requestStream, ServerCallContext context)
        {
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

            }

            return new UploadStatus { Success = true, Message = "File uploaded successfully." };
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