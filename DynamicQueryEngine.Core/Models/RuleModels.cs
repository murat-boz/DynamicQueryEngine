namespace DynamicQueryEngine.Core.Models
{
    public class RuleDefinition
    {
        public string RuleId { get; set; }
        public string Name { get; set; }
        public int Version { get; set; }
        public bool IsActive { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string TargetType { get; set; }
        public ConditionGroup? Conditions { get; set; }
        public List<string>? GroupBy { get; set; } = new();
        public AggregationDefinition? Aggregation { get; set; }
    }

    public class ConditionGroup
    {
        public string LogicalOperator { get; set; } = "AND";
        public List<Condition> Conditions { get; set; } = new();
        public List<ConditionGroup> Groups { get; set; } = new();
    }

    public class Condition
    {
        public string Property { get; set; }
        public string Operator { get; set; }
        public object Value { get; set; }
    }

    public enum AggregateFunction
    {
        Min,
        Max,
        Count
    }

    public class AggregationDefinition
    {
        public string? AggregateProperty { get; set; }
        public AggregateFunction AggregateFunction { get; set; }
    }
}
