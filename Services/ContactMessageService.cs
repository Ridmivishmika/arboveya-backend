using System.Web;
using Arboveya.Api.Data;
using Arboveya.Api.DTOs;
using Arboveya.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Arboveya.Api.Services;

public class ContactMessageService : IContactMessageService
{
    private readonly AppDbContext _context;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContactMessageService> _logger;

    public ContactMessageService(
        AppDbContext context,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<ContactMessageService> logger)
    {
        _context = context;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ContactMessageResponseDto> SubmitMessageAsync(Guid? userId, CreateContactMessageDto dto)
    {
        string name = dto.Name.Trim();
        string email = dto.Email.Trim().ToLowerInvariant();
        string? phone = string.IsNullOrWhiteSpace(dto.PhoneNumber) ? null : dto.PhoneNumber.Trim();
        string userType = !string.IsNullOrWhiteSpace(dto.UserType) ? dto.UserType.Trim() : "General";

        // If authenticated user, fallback to user profile if fields are empty
        if (userId.HasValue)
        {
            var user = await _context.Users.FindAsync(userId.Value);
            if (user != null)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"{user.FirstName} {user.LastName}".Trim();
                }

                if (string.IsNullOrWhiteSpace(email))
                {
                    email = user.Email;
                }

                if (phone == null && !string.IsNullOrWhiteSpace(user.PhoneNumber))
                {
                    phone = user.PhoneNumber.Trim();
                }

                // If user didn't specify or selected general, auto-tag with their actual role
                if (string.IsNullOrWhiteSpace(dto.UserType) || dto.UserType.Equals("General", StringComparison.OrdinalIgnoreCase))
                {
                    userType = user.Role == "Seller" ? "Seller" : user.Role == "Admin" ? "Admin" : "Buyer";
                }
            }
        }

        var message = new ContactMessage
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            Email = email,
            PhoneNumber = phone,
            UserType = userType,
            Subject = dto.Subject.Trim(),
            Message = dto.Message.Trim(),
            Status = "Unread",
            CreatedAt = DateTime.UtcNow
        };

        _context.ContactMessages.Add(message);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Contact message '{Subject}' ({Id}) submitted by [{UserType}] {Email}.", 
            message.Subject, message.Id, message.UserType, message.Email);

        // Send email notification to Admin's email
        try
        {
            var adminEmail = (_configuration["Admin:Email"]
                ?? Environment.GetEnvironmentVariable("ADMIN_EMAIL")
                ?? "admin@arboveya.com").Trim();

            var safeMessageBody = HttpUtility.HtmlEncode(message.Message).Replace("\n", "<br/>");
            var emailSubject = $"[Arboveya {message.UserType} Inquiry] {message.Subject}";

            var htmlBody = $@"
<!DOCTYPE html>
<html>
<head>
  <meta charset=""utf-8"">
  <style>
    body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background-color: #f4f7f4; margin: 0; padding: 24px; color: #1c3f24; }}
    .container {{ max-width: 600px; margin: 0 auto; background: #ffffff; border-radius: 12px; overflow: hidden; border: 1px solid #dce6dc; box-shadow: 0 4px 12px rgba(0,0,0,0.05); }}
    .header {{ background: #24492d; color: #ffffff; padding: 28px 24px; text-align: center; }}
    .header h1 {{ margin: 0; font-size: 20px; letter-spacing: 2px; font-weight: 700; }}
    .header p {{ margin: 6px 0 0; font-size: 12px; opacity: 0.85; text-transform: uppercase; letter-spacing: 1px; }}
    .content {{ padding: 28px 24px; }}
    .badge {{ display: inline-block; padding: 4px 12px; border-radius: 9999px; font-size: 11px; font-weight: bold; text-transform: uppercase; background: #e6f2e8; color: #1c3f24; border: 1px solid #b8dabf; }}
    .badge-seller {{ background: #fef3c7; color: #92400e; border-color: #fde68a; }}
    .badge-buyer {{ background: #e0f2fe; color: #075985; border-color: #bae6fd; }}
    .details-table {{ width: 100%; border-collapse: collapse; margin: 20px 0; font-size: 13px; }}
    .details-table td {{ padding: 8px 0; border-bottom: 1px solid #f0f4f0; }}
    .details-table td.label {{ width: 120px; font-weight: 600; color: #4b6350; }}
    .details-table td.val {{ color: #111827; }}
    .message-box {{ background: #f8faf8; border-left: 4px solid #24492d; padding: 16px 20px; border-radius: 6px; margin-top: 15px; font-size: 14px; line-height: 1.6; color: #1f2937; }}
    .footer {{ background: #f9fbf9; border-top: 1px solid #eef2ee; padding: 18px 24px; font-size: 12px; color: #6b7280; text-align: center; }}
    .footer a {{ color: #24492d; font-weight: 600; text-decoration: none; }}
  </style>
</head>
<body>
  <div class=""container"">
    <div class=""header"">
      <h1>🌿 ARBOVEYA APOTHECARY</h1>
      <p>Customer & Merchant Support Center</p>
    </div>
    <div class=""content"">
      <div style=""display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px;"">
        <span class=""badge {(message.UserType == "Seller" ? "badge-seller" : message.UserType == "Buyer" ? "badge-buyer" : "")}"">
          {message.UserType} Inquiry
        </span>
        <span style=""font-size: 11px; color: #6b7280;"">{message.CreatedAt:MMM dd, yyyy HH:mm} UTC</span>
      </div>

      <h2 style=""margin: 8px 0 16px; font-size: 17px; color: #1c3f24;"">
        {HttpUtility.HtmlEncode(message.Subject)}
      </h2>

      <table class=""details-table"">
        <tr>
          <td class=""label"">Sender Name:</td>
          <td class=""val""><strong>{HttpUtility.HtmlEncode(message.Name)}</strong></td>
        </tr>
        <tr>
          <td class=""label"">Email:</td>
          <td class=""val""><a href=""mailto:{message.Email}"" style=""color: #24492d; font-weight: 600;"">{HttpUtility.HtmlEncode(message.Email)}</a></td>
        </tr>
        <tr>
          <td class=""label"">Phone:</td>
          <td class=""val"">{(string.IsNullOrWhiteSpace(message.PhoneNumber) ? "Not provided" : HttpUtility.HtmlEncode(message.PhoneNumber))}</td>
        </tr>
        <tr>
          <td class=""label"">Inquiry ID:</td>
          <td class=""val"" style=""font-family: monospace; font-size: 11px;"">{message.Id}</td>
        </tr>
      </table>

      <div style=""font-size: 12px; font-weight: 600; color: #4b6350; text-transform: uppercase; letter-spacing: 0.5px; margin-top: 20px;"">
        Message Content:
      </div>
      <div class=""message-box"">
        {safeMessageBody}
      </div>
    </div>
    <div class=""footer"">
      This inquiry was delivered to the Arboveya administrator mailbox (<strong>{adminEmail}</strong>).<br/>
      You can reply directly to this email to respond directly to <strong>{HttpUtility.HtmlEncode(message.Name)}</strong>.
    </div>
  </div>
</body>
</html>";

            await _emailService.SendEmailAsync(adminEmail, emailSubject, htmlBody, message.Email);
            _logger.LogInformation("Notification email dispatched to admin {AdminEmail} for contact message {Id}.", adminEmail, message.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch email notification to Admin for contact message {Id}. The message was saved to database.", message.Id);
        }

        return MapToDto(message);
    }

    public async Task<IEnumerable<ContactMessageResponseDto>> GetAllMessagesAsync(string? status = null, string? search = null)
    {
        var query = _context.ContactMessages
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(m => m.Status.ToLower() == status.Trim().ToLower());
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(m => EF.Functions.ILike(m.Name, $"%{trimmedSearch}%") ||
                                     EF.Functions.ILike(m.Email, $"%{trimmedSearch}%") ||
                                     EF.Functions.ILike(m.Subject, $"%{trimmedSearch}%") ||
                                     EF.Functions.ILike(m.Message, $"%{trimmedSearch}%") ||
                                     EF.Functions.ILike(m.UserType, $"%{trimmedSearch}%"));
        }

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => MapToDto(m))
            .ToListAsync();

        return messages;
    }

    public async Task<ContactMessageResponseDto?> GetByIdAsync(Guid id, bool markAsRead = true)
    {
        var message = await _context.ContactMessages.FindAsync(id);
        if (message == null)
        {
            return null;
        }

        if (markAsRead && message.Status.Equals("Unread", StringComparison.OrdinalIgnoreCase))
        {
            message.Status = "Read";
            message.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            _logger.LogInformation("Contact message {Id} marked as Read.", id);
        }

        return MapToDto(message);
    }

    public async Task<ContactMessageResponseDto?> UpdateStatusAsync(Guid id, UpdateContactMessageStatusDto dto)
    {
        var message = await _context.ContactMessages.FindAsync(id);
        if (message == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(dto.Status))
        {
            message.Status = dto.Status.Trim();
        }

        if (dto.AdminNotes != null)
        {
            message.AdminNotes = string.IsNullOrWhiteSpace(dto.AdminNotes) ? null : dto.AdminNotes.Trim();
        }

        message.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Contact message {Id} status updated to '{Status}'.", id, message.Status);

        return MapToDto(message);
    }

    public async Task<bool> DeleteMessageAsync(Guid id)
    {
        var message = await _context.ContactMessages.FindAsync(id);
        if (message == null)
        {
            return false;
        }

        _context.ContactMessages.Remove(message);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Contact message {Id} deleted.", id);

        return true;
    }

    private static ContactMessageResponseDto MapToDto(ContactMessage m)
    {
        return new ContactMessageResponseDto
        {
            Id = m.Id,
            UserId = m.UserId,
            Name = m.Name,
            Email = m.Email,
            PhoneNumber = m.PhoneNumber,
            UserType = m.UserType ?? "General",
            Subject = m.Subject,
            Message = m.Message,
            Status = m.Status,
            AdminNotes = m.AdminNotes,
            CreatedAt = m.CreatedAt,
            UpdatedAt = m.UpdatedAt
        };
    }
}
