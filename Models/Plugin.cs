using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("plugins")]
public class Plugin
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("name")]
    public string Name { get; set; } = null!; // es: TRUBENDRCI

    [Required]
    [Column("display_name")]
    public string DisplayName { get; set; } = null!;

    [Column("supports_surveys")]
    public bool SupportsSurveys { get; set; } = true;

    [Column("supports_machinereader")]
    public bool SupportsMachineReader { get; set; } = false;

    [Column("manages_dispatcher")]
    public bool ManagesDispatcher { get; set; } = false;

    [Column("dispatcher_code")]
    public string? DispatcherCode { get; set; } // es: CSB

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
