using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("plugin_scenarios")]
public class PluginScenario
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("plugin_id")]
    public long PluginId { get; set; }

    [Column("name")]
    public string Name { get; set; } = null!;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
