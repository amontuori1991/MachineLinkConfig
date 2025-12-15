using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("plugin_parameter_templates")]
public class PluginParameterTemplate
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("plugin_id")]
    public long PluginId { get; set; }

    [Required]
    [Column("service_root")]
    public string ServiceRoot { get; set; } = null!; // SURVEYS_SERVICE | MACHINEREADER_SERVICE

    [Required]
    [Column("scope")]
    public string Scope { get; set; } = null!; // PLUGIN_GLOBAL | PER_MACHINE

    [Required]
    [Column("param_code")]
    public string ParamCode { get; set; } = null!;

    [Required]
    [Column("key_suffix")]
    public string KeySuffix { get; set; } = null!; // es: MAC, MODE_OPENONMAC...

    [Column("description")]
    public string Description { get; set; } = "";

    [Column("default_cfusr")]
    public string? DefaultCfusr { get; set; }

    [Column("default_cfval")]
    public string? DefaultCfval { get; set; }

    [Column("export_enabled_default")]
    public bool ExportEnabledDefault { get; set; } = true;

    [Column("sort_order")]
    public int SortOrder { get; set; } = 0;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
