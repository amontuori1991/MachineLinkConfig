using MachineLinkConfig.Models;
using Microsoft.EntityFrameworkCore;

namespace MachineLinkConfig.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Plugin> Plugins => Set<Plugin>();
    public DbSet<PluginParameterTemplate> PluginParameterTemplates => Set<PluginParameterTemplate>();
    public DbSet<PluginMachineConfig> PluginMachineConfigs => Set<PluginMachineConfig>();
    public DbSet<PluginParameterValue> PluginParameterValues => Set<PluginParameterValue>();
    public DbSet<PluginRule> PluginRules => Set<PluginRule>();

    public DbSet<DispatcherParameterTemplate> DispatcherParameterTemplates => Set<DispatcherParameterTemplate>();
    public DbSet<DispatcherParameterValue> DispatcherParameterValues => Set<DispatcherParameterValue>();
    public DbSet<PluginScenario> PluginScenarios => Set<PluginScenario>();
    public DbSet<PluginScenarioFlag> PluginScenarioFlags => Set<PluginScenarioFlag>();
    public DbSet<MachineLinkConfig.Models.GlobalRule> GlobalRules => Set<MachineLinkConfig.Models.GlobalRule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<MachineLinkConfig.Models.GlobalRule>(e =>
        {
            e.ToTable("GlobalRules");
            e.Property(x => x.RuleType).IsRequired();
            e.Property(x => x.IfParamKeySuffix).IsRequired();
            e.Property(x => x.ThenParamKeySuffix).IsRequired();
            e.Property(x => x.Description).IsRequired();
        });

        // Non aggiungo relazioni complesse ora: mapping semplice e robusto.
        // Le FK sono già in DB: EF le userà quando faremo query join.
    }

}
