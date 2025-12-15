using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MachineLinkConfig.Models;

[Table("plugin_scenario_flags")]
public class PluginScenarioFlag
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("scenario_id")]
    public long ScenarioId { get; set; }

    [Column("flag_code")]
    public string FlagCode { get; set; } = null!;  // es TEMPO_UOMO

    [Column("enabled")]
    public bool Enabled { get; set; }
}
