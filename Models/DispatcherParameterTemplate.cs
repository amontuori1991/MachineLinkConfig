using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("dispatcher_parameter_templates")]
public class DispatcherParameterTemplate
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("plugin_id")]
    public long PluginId { get; set; }

    [Required]
    [Column("param_code")]
    public string ParamCode { get; set; } = null!; // IP, PORT, PROGRAM_ADDRESS

    [Column("description")]
    public string Description { get; set; } = "";

    [Column("default_value")]
    public string? DefaultValue { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; } = 0;
}
