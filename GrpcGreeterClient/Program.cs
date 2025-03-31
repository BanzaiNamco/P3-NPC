using Grpc.Net.Client;
using System.IO;
using MediaUpload;

//// The port number must match the port of the gRPC server.
using var channel = GrpcChannel.ForAddress("https://localhost:7280");
var client = new MediaUpload.MediaUpload.MediaUploadClient(channel);

var filePath = "hint_1.mp4";
using var fileStream = File.OpenRead(filePath);
using var call = client.UploadMedia();

var buffer = new byte[64 * 1024]; // 64 KB buffer
int bytesRead;
while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
{
    var chunk = new VideoChunk
    {
        FileName = Path.GetFileName(filePath),
        Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead)
    };

    await call.RequestStream.WriteAsync(chunk);
}

await call.RequestStream.CompleteAsync();
var response = await call.ResponseAsync;

Console.WriteLine($"Upload status: {response.Message}");