namespace Arboveya.Api.Services;

public interface ICloudStorageService
{
    /// <summary>
    /// Uploads an image file to Cloudinary cloud storage, returning the permanent secure HTTPS URL.
    /// If Cloudinary is not configured, it gracefully falls back to local server storage.
    /// </summary>
    Task<string> UploadImageAsync(IFormFile file, string folder = "arboveya");
}
