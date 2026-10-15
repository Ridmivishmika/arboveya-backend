using System.Net;
using System.Net.Mail;
using Arboveya.Api.Extensions;

namespace Arboveya.Api.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlBody, string? replyToEmail = null)
    {
        var smtpHost = _configuration.ResolveEnv("Email:SmtpHost", "SMTP_HOST", "Email__SmtpHost");
        var smtpPortStr = _configuration.ResolveEnv("Email:SmtpPort", "SMTP_PORT", "Email__SmtpPort");
        var smtpUser = _configuration.ResolveEnv("Email:SmtpUser", "SMTP_USER", "Email__SmtpUser");
        var smtpPass = _configuration.ResolveEnv("Email:SmtpPass", "SMTP_PASS", "Email__SmtpPass");
        var fromEmail = _configuration.ResolveEnv("Email:FromEmail", "SMTP_FROM", "Email__FromEmail")
            ?? (string.IsNullOrWhiteSpace(smtpUser) ? "care@arboveya.com" : smtpUser);
        var fromName = _configuration.ResolveEnv("Email:FromName", "SMTP_FROM_NAME", "Email__FromName")
            ?? "Arboveya";

        int smtpPort = 587;
        if (!string.IsNullOrWhiteSpace(smtpPortStr) && int.TryParse(smtpPortStr, out var parsedPort))
        {
            smtpPort = parsedPort;
        }

        // If SMTP configuration is not fully provided, simulate delivery and log clearly
        if (string.IsNullOrWhiteSpace(smtpHost) || string.IsNullOrWhiteSpace(smtpUser) || string.IsNullOrWhiteSpace(smtpPass))
        {
            _logger.LogInformation(
                "==============================================================\n" +
                "[EmailService Simulation] Email dispatch to Admin:\n" +
                "To: {ToEmail}\n" +
                "From: {FromEmail} ({FromName})\n" +
                "Reply-To: {ReplyTo}\n" +
                "Subject: {Subject}\n" +
                "Note: Configure SMTP_HOST, SMTP_USER, SMTP_PASS in .env for live transport.\n" +
                "==============================================================",
                toEmail, fromEmail, fromName, replyToEmail ?? "N/A", subject);

            return true;
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };

            message.To.Add(new MailAddress(toEmail));

            if (!string.IsNullOrWhiteSpace(replyToEmail))
            {
                message.ReplyToList.Add(new MailAddress(replyToEmail));
            }

            using var client = new SmtpClient(smtpHost, smtpPort)
            {
                Credentials = new NetworkCredential(smtpUser, smtpPass),
                EnableSsl = true,
                Timeout = 15000 // 15 seconds
            };

            await client.SendMailAsync(message);
            _logger.LogInformation("Live email successfully sent to {ToEmail} regarding '{Subject}'.", toEmail, subject);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send live email to {ToEmail} via SMTP {Host}:{Port}. Email logged for fallback.", 
                toEmail, smtpHost, smtpPort);
            return false;
        }
    }
}
