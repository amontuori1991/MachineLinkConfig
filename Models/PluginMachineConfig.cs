using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("plugin_machine_configs")]
public class PluginMachineConfig
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("plugin_id")]
    public long PluginId { get; set; }

    [Column("machine_no")]
    public int MachineNo { get; set; } // 1,2,3 -> export 01,02,03

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
