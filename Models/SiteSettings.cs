using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Arboveya.Api.Models;

[Table("SiteSettings")]
public class SiteSettings
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = 1;

    [MaxLength(300)]
    public string? HomePageHeroText { get; set; }

    [MaxLength(300)]
    public string? AboutHeroSubtitle { get; set; }

    public string? AboutUsContent { get; set; }

    public string? Mission { get; set; }

    public string? Vision { get; set; }

    [MaxLength(500)]
    public string? FacebookLink { get; set; }

    [MaxLength(50)]
    public string? WhatsAppNumber { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

