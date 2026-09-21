namespace Arboveya.Api.Services;

public interface IEmailService
{
    /// <summary>
    /// Sends an email asynchronously to the specified recipient.
    /// In local development or when SMTP is unconfigured, logs the email details gracefully.
    /// </summary>
    /// <param name="toEmail">Destination email address</param>
    /// <param name="subject">Email subject line</param>
    /// <param name="htmlBody">HTML formatted body content</param>
    /// <param name="replyToEmail">Optional sender email address for direct replies</param>
    Task<bool> SendEmailAsync(string toEmail, string subject, string htmlBody, string? replyToEmail = null);
}
