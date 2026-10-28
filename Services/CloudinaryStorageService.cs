using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace Arboveya.Api.Services;

public class CloudinaryStorageService : ICloudStorageService
{
    private readonly Cloudinary? _cloudinary;
    private readonly ILogger<CloudinaryStorageService> _logger;
    private readonly IWebHostEnvironment _environment;

    public CloudinaryStorageService(
        IConfiguration configuration,
        ILogger<CloudinaryStorageService> logger,
        IWebHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;

        // 1. Check CLOUDINARY_URL first
        var cloudinaryUrl = configuration["Cloudinary:Url"]
            ?? configuration["CLOUDINARY_URL"]
            ?? Environment.GetEnvironmentVariable("CLOUDINARY_URL");

        if (!string.IsNullOrWhiteSpace(cloudinaryUrl))
        {
            try
            {
                _cloudinary = new Cloudinary(cloudinaryUrl);
                _cloudinary.Api.Secure = true;
                _logger.LogInformation("Cloudinary initialized successfully using CLOUDINARY_URL.");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize Cloudinary from CLOUDINARY_URL. Checking individual keys.");
            }
        }

        // 2. Check individual Cloudinary credentials
        var cloudName = configuration["Cloudinary:CloudName"]
            ?? configuration["CLOUDINARY_CLOUD_NAME"]
            ?? Environment.GetEnvironmentVariable("CLOUDINARY_CLOUD_NAME");

        var apiKey = configuration["Cloudinary:ApiKey"]
            ?? configuration["CLOUDINARY_API_KEY"]
            ?? Environment.GetEnvironmentVariable("CLOUDINARY_API_KEY");

        var apiSecret = configuration["Cloudinary:ApiSecret"]
            ?? configuration["CLOUDINARY_API_SECRET"]
            ?? Environment.GetEnvironmentVariable("CLOUDINARY_API_SECRET");

        if (!string.IsNullOrWhiteSpace(cloudName) &&
            !string.IsNullOrWhiteSpace(apiKey) &&
            !string.IsNullOrWhiteSpace(apiSecret))
        {
            var account = new Account(cloudName.Trim(), apiKey.Trim(), apiSecret.Trim());
            _cloudinary = new Cloudinary(account);
            _cloudinary.Api.Secure = true;
            _logger.LogInformation("Cloudinary initialized successfully with CloudName '{CloudName}'.", cloudName);
        }
        else
        {
            _logger.LogWarning("Cloudinary credentials not detected (CloudName, ApiKey, ApiSecret). Falling back to local storage.");
        }
    }

    public async Task<string> UploadImageAsync(IFormFile file, string folder = "arboveya")
    {
        if (file == null || file.Length == 0)
        {
            throw new ArgumentException("Please select an image file to upload.", nameof(file));
        }

        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            throw new ArgumentException("Only image formats (.jpg, .jpeg, .png, .webp, .gif) are supported.");
        }

        if (file.Length > 10 * 1024 * 1024)
        {
            throw new ArgumentException("Image size cannot exceed 10MB.");
        }

        // A. Upload to Cloudinary if configured
        if (_cloudinary != null)
        {
            try
            {
                await using var stream = file.OpenReadStream();
                var uploadParams = new ImageUploadParams
                {
                    File = new FileDescription(file.FileName, stream),
                    Folder = folder,
                    UseFilename = true,
                    UniqueFilename = true,
                    Overwrite = false
                };

                var uploadResult = await _cloudinary.UploadAsync(uploadParams);
                if (uploadResult.Error != null)
                {
                    _logger.LogError("Cloudinary upload failed: {Message}", uploadResult.Error.Message);
                    throw new InvalidOperationException($"Cloudinary upload failed: {uploadResult.Error.Message}");
                }

                var secureUrl = uploadResult.SecureUrl?.ToString() ?? uploadResult.Url?.ToString();
                if (!string.IsNullOrWhiteSpace(secureUrl))
                {
                    _logger.LogInformation("Image uploaded to Cloudinary: {Url}", secureUrl);
                    return secureUrl;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error uploading to Cloudinary. Falling back to local storage.");
            }
        }

        // B. Fallback to Local Disk Storage
        var webRootPath = _environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRootPath))
        {
            webRootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        }

        var uploadsFolder = Path.Combine(webRootPath, "uploads");
        if (!Directory.Exists(uploadsFolder))
        {
            Directory.CreateDirectory(uploadsFolder);
        }

        var uniqueFileName = $"{Guid.NewGuid():N}{ext}";
        var filePath = Path.Combine(uploadsFolder, uniqueFileName);

        await using (var fileStream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(fileStream);
        }

        return $"/uploads/{uniqueFileName}";
    }
}
