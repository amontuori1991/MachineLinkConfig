using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("plugin_parameter_values")]
public class PluginParameterValue
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("plugin_id")]
    public long PluginId { get; set; }

    [Column("template_id")]
    public long TemplateId { get; set; }

    [Column("machine_config_id")]
    public long? MachineConfigId { get; set; } // null se PLUGIN_GLOBAL

    [Column("cfusr")]
    public string? Cfusr { get; set; }

    [Column("cfval")]
    public string? Cfval { get; set; }

    [Column("export_enabled")]
    public bool ExportEnabled { get; set; } = true;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
