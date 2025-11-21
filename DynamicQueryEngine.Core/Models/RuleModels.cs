namespace DynamicQueryEngine.Core.Models
{
    public class RuleDefinition
    {
        public string Name { get; set; }
        public string? Comment { get; set; }
        public double Version { get; set; }
        public bool IsActive { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string SourceType { get; set; }
        public string TargetType { get; set; }
        public IntegrationBinding? Integration { get; set; }
        public ConditionGroup? Conditions { get; set; }
        public List<string>? GroupBy { get; set; } = new();
        public AggregationDefinition? Aggregation { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class ConditionGroup
    {
        public string LogicalOperator { get; set; } = "AND";
        public List<Condition> Conditions { get; set; } = new();
        public List<ConditionGroup> Groups { get; set; } = new();
        public bool Negate { get; set; } = false;
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

public class IntegrationBinding
{
    public string? CompositeId { get; set; }
}