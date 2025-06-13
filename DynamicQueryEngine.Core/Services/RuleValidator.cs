using DynamicQueryEngine.Core.Models;
using System.Reflection;

namespace DynamicQueryEngine.Core.Services;

public static class RuleValidator
{
    public static void Validate<T>(RuleDefinition rule)
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                   .Select(p => p.Name)
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase);

        void ValidateGroup(ConditionGroup? group)
        {
            if (group == null)
            {
                return;
            }

            foreach (var condition in group.Conditions)
            {
                if (!properties.Contains(condition.Property))
                {
                    throw new InvalidOperationException($"Property '{condition.Property}' not found on '{typeof(T).Name}'");
                }
            }

            foreach (var subgroup in group.Groups)
            {
                ValidateGroup(subgroup);
            }
        }

        ValidateGroup(rule.Conditions);
        foreach (var gb in rule.GroupBy)
        {
            if (!properties.Contains(gb))
            {
                throw new InvalidOperationException($"GroupBy field '{gb}' invalid");
            }
        }

        if (rule.GroupBy.Count() > 0 && rule.Aggregation == null)
        {
            throw new InvalidOperationException("Aggregation must be defined when GroupBy is provided.");
        }

        if (rule.Aggregation is not null && !properties.Contains(rule.Aggregation.AggregateProperty))
        {
            throw new InvalidOperationException($"AggregateProperty '{rule.Aggregation.AggregateProperty}' invalid");
        }
    }
}