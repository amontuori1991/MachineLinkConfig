using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("plugin_rules")]
public class PluginRule
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("plugin_id")]
    public long PluginId { get; set; }

    [Required]
    [Column("rule_type")]
    public string RuleType { get; set; } = null!; // MUTEX | REQUIRE | FORCE_VALUE

    [Required]
    [Column("if_param_key_suffix")]
    public string IfParamKeySuffix { get; set; } = null!;

    [Required]
    [Column("then_param_key_suffix")]
    public string ThenParamKeySuffix { get; set; } = null!;

    [Column("forced_value")]
    public string? ForcedValue { get; set; }

    [Column("description")]
    public string Description { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
