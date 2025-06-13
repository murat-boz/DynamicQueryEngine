using DynamicQueryEngine.Core.Models;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace DynamicQueryEngine.Core.Services;

public static class RuleCompiler
{
    public static Expression<Func<T, bool>> CompileRule<T>(ConditionGroup group)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var body = CompileGroupBody<T>(group, parameter);
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static Expression CompileGroupBody<T>(ConditionGroup group, ParameterExpression parameter)
    {
        var expressions = new List<Expression>();
        foreach (var condition in group.Conditions)
            expressions.Add(CompileCondition<T>(condition, parameter));
        foreach (var subgroup in group.Groups)
            expressions.Add(CompileGroupBody<T>(subgroup, parameter));

        return group.LogicalOperator.ToUpper() == "OR" ? expressions.Aggregate(Expression.OrElse) : expressions.Aggregate(Expression.AndAlso);
    }

    private static Expression CompileCondition<T>(Condition condition, ParameterExpression parameter)
    {
        var prop = typeof(T).GetProperty(condition.Property, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
        Expression propertyAccess = Expression.Property(parameter, prop);

        if (prop.PropertyType == typeof(string) && IsNumericOperator(condition.Operator))
        {
            var parseMethod = typeof(decimal).GetMethod(nameof(decimal.Parse), new[] { typeof(string) });
            propertyAccess = Expression.Call(parseMethod, propertyAccess);
        }

        var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        CultureInfo cultureInfo = CultureInfo.CreateSpecificCulture("en-US");

        if (condition.Operator.ToLower(cultureInfo) == "in")
        {
            if (condition.Value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                var list = jsonElement.EnumerateArray().Select(e => e.GetString()).ToList();

                var constants = list
                    .Select(v => Expression.Constant(Convert.ChangeType(v, targetType), targetType))
                    .ToList();

                Expression body = constants
                    .Select(c => Expression.Equal(propertyAccess, c))
                    .Aggregate(Expression.OrElse);

                return body;
            }
            else if (condition.Value is IEnumerable<object> values)
            {
                var constants = values
                    .Select(v => Expression.Constant(Convert.ChangeType(v, targetType), targetType))
                    .ToList();

                Expression body = constants
                    .Select(c => Expression.Equal(propertyAccess, c))
                    .Aggregate(Expression.OrElse);

                return body;
            }
            else
            {
                throw new InvalidOperationException("IN operator expects array value.");
            }
        }
        else
        {
            object val = ExtractValueWithCoercion(condition.Value, targetType, condition.Operator);
            var constant = Expression.Constant(val, val.GetType());

            return condition.Operator.ToLower() switch
            {
                "equal" => Expression.Equal(propertyAccess, constant),
                "notequal" => Expression.NotEqual(propertyAccess, constant),
                "greaterthan" => Expression.GreaterThan(propertyAccess, constant),
                "greaterthanorequal" => Expression.GreaterThanOrEqual(propertyAccess, constant),
                "lessthan" => Expression.LessThan(propertyAccess, constant),
                "lessthanorequal" => Expression.LessThanOrEqual(propertyAccess, constant),
                "contains" => Expression.Call(propertyAccess, typeof(string).GetMethod("Contains", new[] { typeof(string) }), constant),
                "startswith" => Expression.Call(propertyAccess, typeof(string).GetMethod("StartsWith", new[] { typeof(string) }), constant),
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