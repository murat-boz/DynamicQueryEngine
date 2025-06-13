using DynamicQueryEngine.Core.Models;

namespace DynamicQueryEngine.Core.Services;

public static class RuleEvaluator
{
    public static IEnumerable<T> EvaluateRule<T>(IEnumerable<T> data, RuleDefinition rule)
    {
        RuleValidator.Validate<T>(rule);
        var expr = RuleCompiler.CompileRule<T>(rule.Conditions);
        var filtered = data.AsQueryable().Where(expr).ToList();
        return AggregationEngine.ApplyAggregation(filtered, rule);
    }
}