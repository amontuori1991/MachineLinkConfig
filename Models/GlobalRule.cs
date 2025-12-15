namespace MachineLinkConfig.Models
{
    public class GlobalRule
    {
        public long Id { get; set; }

        public string RuleType { get; set; } = "MUTEX"; // MUTEX | REQUIRE | FORCE_VALUE
        public string IfParamKeySuffix { get; set; } = "";
        public string ThenParamKeySuffix { get; set; } = "";

        public string? ForcedValue { get; set; }
        public string Description { get; set; } = "";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
