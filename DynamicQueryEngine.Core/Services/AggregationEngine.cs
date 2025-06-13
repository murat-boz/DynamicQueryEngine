using System.Globalization;
using System.Reflection;
using DynamicQueryEngine.Core.Models;

namespace DynamicQueryEngine.Core.Services;

public static class AggregationEngine
{
    public static IEnumerable<T> ApplyAggregation<T>(IEnumerable<T> data, RuleDefinition rule)
    {
        var grouped = data.GroupBy(item => BuildGroupKey(item, rule.GroupBy));
        foreach (var group in grouped)
            yield return ApplyAggregate(group, rule.Aggregation);
    }

    private static string BuildGroupKey<T>(T item, List<string> groupByProperties)
    {
        var values = groupByProperties.Select(p =>
        {
            var prop = typeof(T).GetProperty(p, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            return prop?.GetValue(item)?.ToString() ?? "";
        });
        return string.Join("::", values);
    }

    private static T ApplyAggregate<T>(IEnumerable<T> group, AggregationDefinition aggregation)
    {
        var prop = typeof(T).GetProperty(aggregation.AggregateProperty, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

        if (aggregation.AggregateFunction.Equals("Min", StringComparison.OrdinalIgnoreCase))
        {
            return group
                .OrderBy(item => ConvertToDecimal(prop.GetValue(item)))
                .First();
        }
        else if (aggregation.AggregateFunction.Equals("Max", StringComparison.OrdinalIgnoreCase))
        {
            return group
                .OrderByDescending(item => ConvertToDecimal(prop.GetValue(item)))
                .First();
        }
        else
        {
            throw new NotSupportedException($"Aggregate function '{aggregation.AggregateFunction}' not supported.");
        }
    }

    private static decimal ConvertToDecimal(object value)
    {
        if (value is string s)
            return decimal.Parse(s, CultureInfo.InvariantCulture);
        if (value is int i)
            return i;
        if (value is long l)
            return l;
        if (value is decimal d)
            return d;

        return Convert.ToDecimal(value);
    }

}