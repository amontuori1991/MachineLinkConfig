using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("dispatcher_parameter_values")]
public class DispatcherParameterValue
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("plugin_id")]
    public long PluginId { get; set; }

    [Column("machine_config_id")]
    public long MachineConfigId { get; set; }

    [Column("template_id")]
    public long TemplateId { get; set; }

    [Required]
    [Column("company_code")]
    public string CompanyCode { get; set; } = "A01"; // CFUSR fisso sul dispatcher

    [Column("value")]
    public string? Value { get; set; }

    [Column("export_enabled")]
    public bool ExportEnabled { get; set; } = true;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
