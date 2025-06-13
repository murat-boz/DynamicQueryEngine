using DynamicQueryEngine.Core.Models;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace DynamicQueryEngine.Core.Services
{
    public static class IQueryableRuleEvaluator
    {
        public static IQueryable<T> ApplyRule<T>(this IQueryable<T> source, RuleDefinition rule)
        {
            RuleValidator.Validate<T>(rule);

            IQueryable<T> filtered = source;

            // Filter Expression
            var parameter = Expression.Parameter(typeof(T), "x");

            // Condition varsa filtre uygula
            if (rule.Conditions != null
                && (rule.Conditions.Conditions.Any() || rule.Conditions.Groups.Any()))
            {
                var filter = BuildFilter<T>(rule.Conditions, parameter);
                filtered = filtered.Where(filter);
            }

            // If no GroupBy & Aggregation, return filtered directly
            if (rule.GroupBy == null || rule.GroupBy.Count == 0 || rule.Aggregation == null)
            {
                return filtered;
            }

            // GroupBy
            var groupByProp = Expression.Property(parameter, rule.GroupBy.First());
            var groupBySelector = Expression.Lambda<Func<T, object>>(Expression.Convert(groupByProp, typeof(object)), parameter);

            // Aggregate
            var aggregateProp = Expression.Property(parameter, rule.Aggregation.AggregateProperty);
            var aggregateSelector = Expression.Lambda<Func<T, object>>(Expression.Convert(aggregateProp, typeof(object)), parameter);

            var query = filtered
                .GroupBy(groupBySelector)
                .Select(g => g.AsQueryable().OrderBy(aggregateSelector).First());

            return query;
        }

        private static Expression<Func<T, bool>> BuildFilter<T>(ConditionGroup group, ParameterExpression parameter)
        {
            Expression body = BuildGroupBody<T>(group, parameter);
            return Expression.Lambda<Func<T, bool>>(body, parameter);
        }

        private static Expression BuildGroupBody<T>(ConditionGroup group, ParameterExpression parameter)
        {
            var expressions = new List<Expression>();
            foreach (var condition in group.Conditions)
                expressions.Add(BuildCondition<T>(condition, parameter));

            foreach (var subgroup in group.Groups)
                expressions.Add(BuildGroupBody<T>(subgroup, parameter));

            return group.LogicalOperator.ToUpperInvariant() == "OR" ? expressions.Aggregate(Expression.OrElse) : expressions.Aggregate(Expression.AndAlso);
        }

        private static Expression BuildCondition<T>(Condition condition, ParameterExpression parameter)
        {
            var prop = typeof(T).GetProperty(condition.Property, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            Expression propertyAccess = Expression.Property(parameter, prop);

            if (prop.PropertyType == typeof(string) && IsNumericOperator(condition.Operator))
            {
                var parseMethod = typeof(decimal).GetMethod(nameof(decimal.Parse), new[] { typeof(string) });
                propertyAccess = Expression.Call(parseMethod, propertyAccess);
            }

            var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            var op = condition.Operator.ToLowerInvariant();

            if (op == "in")
            {
                if (condition.Value is JsonElement je && je.ValueKind == JsonValueKind.Array)
                {
                    var list = je.EnumerateArray().Select(e => e.GetString()).ToList();
                    var constants = list.Select(v => Expression.Constant(Convert.ChangeType(v, targetType), targetType)).ToList();
                    return constants.Select(c => Expression.Equal(propertyAccess, c)).Aggregate(Expression.OrElse);
                }
                throw new InvalidOperationException("IN operator expects array.");
            }
            else
            {
                object val = ExtractValueWithCoercion(condition.Value, targetType, condition.Operator);
                var constant = Expression.Constant(val, val.GetType());

                return op switch
                {
                    "equal" => Expression.Equal(propertyAccess, constant),
                    "notequal" => Expression.NotEqual(propertyAccess, constant),
                    "greaterthan" => Expression.GreaterThan(propertyAccess, constant),
                    "greaterthanorequal" => Expression.GreaterThanOrEqual(propertyAccess, constant),
                    "lessthan" => Expression.LessThan(propertyAccess, constant),
                    "lessthanorequal" => Expression.LessThanOrEqual(propertyAccess, constant),
                    _ => throw new NotSupportedException()
                };
            }
        }

        private static object ExtractValueWithCoercion(object rawValue, Type propertyType, string operatorName)
        {
            Type targetType = propertyType;

            if (propertyType == typeof(string) && IsNumericOperator(operatorName))
                targetType = typeof(decimal);

            if (rawValue is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number)
                    return Convert.ChangeType(je.GetDouble(), targetType);
                if (je.ValueKind == JsonValueKind.String)
                    return Convert.ChangeType(je.GetString(), targetType);
                if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False) 
                    return je.GetBoolean();
            }

            return Convert.ChangeType(rawValue, targetType);
        }

        private static bool IsNumericOperator(string op) =>
            op.Equals("greaterthan", StringComparison.OrdinalIgnoreCase)
            || op.Equals("greaterthanorequal", StringComparison.OrdinalIgnoreCase)
            || op.Equals("lessthan", StringComparison.OrdinalIgnoreCase)
            || op.Equals("lessthanorequal", StringComparison.OrdinalIgnoreCase);
    }
}
