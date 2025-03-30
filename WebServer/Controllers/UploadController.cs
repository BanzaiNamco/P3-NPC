using Microsoft.AspNetCore.Mvc;
using System.IO;
using System.Threading.Tasks;

[Route("api/upload")]
[ApiController]
public class UploadController : ControllerBase {
    private static Queue<string> videoQueue = new Queue<string>();
    private static readonly int MaxQueueSize = 10; // Limit queue size

    [HttpPost]
    public async Task<IActionResult> UploadVideo([FromForm] IFormFile file) {
        if (file == null || file.Length == 0)
            return BadRequest("Invalid file");

        string filePath = Path.Combine("UploadedVideos", file.FileName);

        // Check queue capacity
        if (videoQueue.Count >= MaxQueueSize)
            return StatusCode(429, "Queue Full");

        // Save file to disk
        Directory.CreateDirectory("UploadedVideos");
        using (var stream = new FileStream(filePath, FileMode.Create)) {
            await file.CopyToAsync(stream);
        }

        // Add file path to queue
        videoQueue.Enqueue(filePath);
        return Ok(new { message = "File uploaded successfully", filePath });
    }

    [HttpGet("queue")]
    public IActionResult GetQueueStatus() {
        return Ok(new { queueSize = videoQueue.Count, maxSize = MaxQueueSize });
    }
}
