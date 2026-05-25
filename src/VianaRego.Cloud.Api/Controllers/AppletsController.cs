using Microsoft.AspNetCore.Mvc;

namespace VianaRego.Cloud.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AppletsController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<AppletsController> _logger;

        public AppletsController(IWebHostEnvironment env, ILogger<AppletsController> logger)
        {
            _env = env;
            _logger = logger;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            // Allow only specific extensions for security (basic check)
            var allowedExtensions = new[] { ".exe", ".zip", ".msi", ".ps1", ".bat" };
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(ext))
                return BadRequest($"File type '{ext}' not allowed.");

            try
            {
                var uploadsPath = Path.Combine(_env.WebRootPath, "applets");
                if (!Directory.Exists(uploadsPath)) Directory.CreateDirectory(uploadsPath);

                // Sanitize filename
                var filename = Path.GetFileName(file.FileName);
                var filePath = Path.Combine(uploadsPath, filename);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                _logger.LogInformation($"File uploaded: {filename} ({file.Length} bytes)");

                // Construct accessible URL
                var protocol = Request.Scheme;
                var host = Request.Host;
                var url = $"{protocol}://{host}/applets/{filename}";

                return Ok(new { url, size = file.Length, filename });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload failed");
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }
    }
}
