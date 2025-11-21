using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicQueryEngine.Core.Models;

namespace DynamicQueryEngine.Core.Services
{
    public static class IQueryableRuleEvaluator
    {
        public static IQueryable<object> ApplyRule<T>(
            this IQueryable<T> source,
            RuleDefinition rule,
            IDictionary<string, object>? externalParams = null)
        {
            RuleValidator.Validate<T>(rule);

            IQueryable<T> filtered = source;

            var parameter = Expression.Parameter(typeof(T), "x");

            // Filtre varsa uygula
            if (rule.Conditions != null &&
                (rule.Conditions.Conditions.Any() || rule.Conditions.Groups.Any()))
            {
                var filter = BuildFilter<T>(rule.Conditions, parameter, externalParams);
                filtered = filtered.Where(filter);
            }

            // Aggregation ve GroupBy yoksa doğrudan döndür
            if (rule.GroupBy == null || rule.GroupBy.Count == 0 || rule.Aggregation == null)
            {
                return filtered.Cast<object>();
            }

            // GroupBy selector (tek property destekleniyor)
            var singleGroupByProp = rule.GroupBy.First();
            var groupByPropExpr = Expression.Property(parameter, singleGroupByProp);
            var groupBySelectorSingle = Expression.Lambda<Func<T, object>>(
                Expression.Convert(groupByPropExpr, typeof(object)), parameter);

            // Aggregate selector
            if (string.IsNullOrEmpty(rule.Aggregation.AggregateProperty))
            {
                throw new InvalidOperationException("AggregateProperty must be provided for Min/Max aggregation.");
            }

            var aggregateProp = Expression.Property(parameter, rule.Aggregation.AggregateProperty);
            var aggregateSelector = Expression.Lambda<Func<T, object>>(
                Expression.Convert(aggregateProp, typeof(object)), parameter);

            var query = filtered
                .GroupBy(groupBySelectorSingle)
                .Select(g => ApplyAggregate(g.AsQueryable(), aggregateSelector, rule.Aggregation));

            return query;
        }

        private static object ApplyAggregate<T>(
            IQueryable<T> group,
            Expression<Func<T, object>> aggregateSelector,
            AggregationDefinition aggregation)
        {
            switch (aggregation.AggregateFunction)
            {
                case AggregateFunction.Min:
                    return group.OrderBy(aggregateSelector).First();

                case AggregateFunction.Max:
                    return group.OrderByDescending(aggregateSelector).First();

                default:
                    throw new NotSupportedException($"Aggregate function '{aggregation.AggregateFunction}' not supported.");
            }
        }

        private static Expression<Func<T, bool>> BuildFilter<T>(
            ConditionGroup group,
            ParameterExpression parameter,
            IDictionary<string, object>? externalParams = null)
        {
            Expression body = BuildGroupBody<T>(group, parameter, externalParams);
            return Expression.Lambda<Func<T, bool>>(body, parameter);
        }

        private static Expression BuildGroupBody<T>(
            ConditionGroup group,
            ParameterExpression parameter,
            IDictionary<string, object>? externalParams = null)
        {
            var expressions = new List<Expression>();

            foreach (var condition in group.Conditions)
            {
                expressions.Add(BuildCondition<T>(condition, parameter, externalParams));
            }

            foreach (var subgroup in group.Groups)
            {
                expressions.Add(BuildGroupBody<T>(subgroup, parameter, externalParams));
            }

            if (!expressions.Any())
            {
                return Expression.Constant(true);
            }

            Expression body = group.LogicalOperator.ToUpperInvariant() == "OR"
                ? expressions.Aggregate(Expression.OrElse)
                : expressions.Aggregate(Expression.AndAlso);

            if (group.Negate)
            {
                body = Expression.Not(body);
            }

            return body;
        }

        private static Expression BuildCondition<T>(
            Condition condition,
            ParameterExpression parameter,
            IDictionary<string, object>? externalParams = null)
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

            //JsonElement je = (JsonElement)condition.Value;

            //if (je.ValueKind == JsonValueKind.Array)
            //{
            //    if (op == nameof(SqlComparableOperator.In).ToLowerInvariant() ||
            //        op == nameof(SqlComparableOperator.NotIn).ToLowerInvariant())
            //    {
            //        var list = je.EnumerateArray().Select(e => e.GetString()).ToList();
            //        var constants = list.Select(v => Expression.Constant(Convert.ChangeType(v, targetType), targetType)).ToList();

            //        var comparisons = constants.Select(c => Expression.Equal(propertyAccess, c));
            //        var combined = comparisons.Aggregate(Expression.OrElse);

            //        return op == nameof(SqlComparableOperator.NotIn).ToLowerInvariant()
            //            ? Expression.Not(combined)
            //            : combined;
            //    }
            //}
            //else if (je.ValueKind == JsonValueKind.Null ||
            //         je.ValueKind == JsonValueKind.Undefined)
            //{
            //    ConstantExpression constant = FindExternalConstant(condition, externalParams, propertyAccess);
            //}


            if (op == nameof(SqlComparableOperator.In).ToLowerInvariant() ||
                op == nameof(SqlComparableOperator.NotIn).ToLowerInvariant())
            {
                if (condition.Value is JsonElement je &&
                    je.ValueKind == JsonValueKind.Array)
                {
                    var list = je.EnumerateArray().Select(e => e.GetString()).ToList();
                    var constants = list.Select(v => Expression.Constant(Convert.ChangeType(v, targetType), targetType)).ToList();

                    var comparisons = constants.Select(c => Expression.Equal(propertyAccess, c));
                    var combined = comparisons.Aggregate(Expression.OrElse);

                    return op == nameof(SqlComparableOperator.NotIn).ToLowerInvariant()
                        ? Expression.Not(combined)
                        : combined;
                }

                throw new InvalidOperationException("IN or NOTIN operator expects array.");
            }
            else
            {
                if (op == nameof(MethodBasedOperator.MustContainIfCountIsGreater).ToLowerInvariant())
                {
                    return MustContainIfCountIsGreater(condition, propertyAccess);
                }
                else if (op == nameof(MethodBasedOperator.ContainIfCountIsGreater).ToLowerInvariant())
                {
                    return ContainIfCountIsGreater(condition, propertyAccess);
                }
                else if (op == nameof(MethodBasedOperator.ContainIfCountIsLess).ToLowerInvariant())
                {
                    return ContainIfCountIsLess(condition, propertyAccess);
                }
                else if (op == nameof(MethodBasedOperator.NotEmpty).ToLowerInvariant())
                {
                    return BuildNotEmptyExpression(propertyAccess);
                }
                else if (op == nameof(MethodBasedOperator.Empty).ToLowerInvariant())
                {
                    return Expression.Not(BuildNotEmptyExpression(propertyAccess));
                }
                else if (op == nameof(MethodBasedOperator.NullOrEmpty).ToLowerInvariant())
                {
                    var nullValue = Expression.Equal(propertyAccess, Expression.Constant(null));
                    var emptyValue = Expression.Equal(propertyAccess, Expression.Constant(""));

                    return Expression.OrElse(nullValue, emptyValue);
                }
                else if (op == nameof(MethodBasedOperator.NotNullOrEmpty).ToLowerInvariant())
                {
                    var notNullValue = Expression.NotEqual(propertyAccess, Expression.Constant(null));
                    var notEmptyValue = Expression.NotEqual(propertyAccess, Expression.Constant(""));

                    return Expression.OrElse(notNullValue, notEmptyValue);
                }
                else if (op == nameof(MethodBasedOperator.Null).ToLowerInvariant())
                {
                    var nullValue = Expression.Equal(propertyAccess, Expression.Constant(null));

                    return nullValue;
                }
                else if (op == nameof(MethodBasedOperator.NotNull).ToLowerInvariant())
                {
                    var notNullValue = Expression.NotEqual(propertyAccess, Expression.Constant(null));

                    return notNullValue;
                }
                else if (op == nameof(MethodBasedOperator.If).ToLowerInvariant())
                {
                    return BuildIfExpression<T>(condition, parameter, externalParams);
                }

                ConstantExpression constant = null;

                if (condition.Value is JsonElement element)
                {
                    if (element.ValueKind == JsonValueKind.Null ||
                        element.ValueKind == JsonValueKind.Undefined)
                    {
                        constant = FindExternalConstant(condition, externalParams, propertyAccess);
                    }
                    else
                    {
                        constant = FindValueConstant(condition, targetType);
                    }
                }

                if (op == nameof(MethodBasedOperator.DynamicNullOrEmpty).ToLowerInvariant())
                {
                    var method = typeof(string).GetMethod(nameof(string.IsNullOrWhiteSpace), new[] { typeof(string) });

                    var stringExpr = ToStringExpression(constant);

                    return Expression.Call(method!, stringExpr);
                }
                else if (op == nameof(MethodBasedOperator.DynamicNotNullOrEmpty).ToLowerInvariant())
                {
                    var method = typeof(string).GetMethod(nameof(string.IsNullOrWhiteSpace), new[] { typeof(string) });

                    var stringExpr = ToStringExpression(constant);

                    return Expression.Not(Expression.Call(method!, stringExpr));
                }
                else if (op == nameof(MethodBasedOperator.DynamicNotEmpty).ToLowerInvariant())
                {
                    return BuildNotEmptyExpression(constant);
                }
                else if (op == nameof(MethodBasedOperator.DynamicEmpty).ToLowerInvariant())
                {
                    return Expression.Not(BuildNotEmptyExpression(constant));
                }
                else if (op == nameof(MethodBasedOperator.DynamicEqual).ToLowerInvariant())
                {
                    return Expression.Equal(propertyAccess, constant);
                }

                return op switch
                {
                    "equal" => Expression.Equal(propertyAccess, constant),
                    "notequal" => Expression.NotEqual(propertyAccess, constant),
                    "greaterthan" => Expression.GreaterThan(propertyAccess, constant),
                    "greaterthanorequal" => Expression.GreaterThanOrEqual(propertyAccess, constant),
                    "lessthan" => Expression.LessThan(propertyAccess, constant),
                    "lessthanorequal" => Expression.LessThanOrEqual(propertyAccess, constant),
                    "contains" => Expression.Call(
                        propertyAccess,
                        typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!,
                        constant
                    ),
                    "notcontains" => Expression.Not(
                        Expression.Call(
                            propertyAccess,
                            typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!,
                            constant
                        )
                    ),
                    "startswith" => Expression.Call(
                        propertyAccess,
                        typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!,
                        constant
                    ),
                    "endswith" => Expression.Call(
                        propertyAccess,
                        typeof(string).GetMethod(nameof(string.EndsWith), new[] { typeof(string) })!,
                        constant
                    ),
                    _ => throw new NotSupportedException()
                };
            }
        }

        private static ConstantExpression FindExternalConstant(Condition condition, IDictionary<string, object>? externalParams, Expression propertyAccess)
        {
            if (externalParams == null ||
                !externalParams.TryGetValue(
                    condition.Property,
                    out var externalValue))
            {
                throw new InvalidOperationException($"External parameter '{condition.Property}' is missing for '{condition.Operator}' operator.");
            }

            if (externalValue == null)
            {
                return Expression.Constant(null, typeof(string));
            }

            return Expression.Constant(externalValue, propertyAccess.Type);
        }

        private static ConstantExpression FindValueConstant(Condition condition, Type targetType)
        {
            object val = ExtractValueWithCoercion(condition.Value, targetType, condition.Operator);
            var constant = Expression.Constant(val, val.GetType());
            return constant;
        }

        private static Expression BuildNotEmptyExpression(Expression propertyAccess)
        {
            var type = propertyAccess.Type;

            if (type == typeof(string))
            {
                // !string.IsNullOrEmpty(property)
                var isNullOrEmpty = Expression.Call(
                    typeof(string).GetMethod(nameof(string.IsNullOrEmpty), new[] { typeof(string) })!,
                    propertyAccess
                );
                return Expression.Not(isNullOrEmpty);
            }

            if (typeof(IEnumerable<>).IsAssignableFrom(type))
            {
                // property != null && property.Cast<object>().Any()
                var notNull = Expression.NotEqual(propertyAccess, Expression.Constant(null, type));

                var anyMethod = typeof(Enumerable).GetMethods()
                    .First(m => m.Name == "Any" && m.GetParameters().Length == 1)
                    .MakeGenericMethod(typeof(object));

                var castMethod = typeof(Enumerable).GetMethod(nameof(Enumerable.Cast))!
                    .MakeGenericMethod(typeof(object));

                var castCall = Expression.Call(null, castMethod, propertyAccess);
                var anyCall = Expression.Call(null, anyMethod, castCall);

                return Expression.AndAlso(notNull, anyCall);
            }

            if (Nullable.GetUnderlyingType(type) != null)
            {
                // property.HasValue
                return Expression.Property(propertyAccess, nameof(Nullable<int>.HasValue));
            }

            throw new NotSupportedException($"NotEmpty operator not supported for type: {type}");
        }

        private static Expression BuildIfExpression<T>(
            Condition condition,
            ParameterExpression parameter,
            IDictionary<string, object>? externalParams = null)
        {
            if (condition.Value is not JsonElement valueElement || valueElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Invalid value format for 'If' operator.");

            if (!valueElement.TryGetProperty("Check", out var checkElement) ||
                !valueElement.TryGetProperty("Then", out var thenElement))
                throw new InvalidOperationException("Both 'Check' and 'Then' properties are required for 'If' operator.");

            // Check Condition
            var checkCondition = new Condition
            {
                Operator = checkElement.GetProperty("Operator").GetString()!,
                Property = checkElement.GetProperty("Property").GetString()!,
                Value = checkElement.TryGetProperty("Value", out var val1) ? val1 : default
            };
            var checkExpr = BuildCondition<T>(checkCondition, parameter, externalParams);

            // Then Condition
            var thenCondition = new Condition
            {
                Operator = thenElement.GetProperty("Operator").GetString()!,
                Property = thenElement.GetProperty("Property").GetString()!,
                Value = thenElement.TryGetProperty("Value", out var val2) ? val2 : default
            };
            var thenExpr = BuildCondition<T>(thenCondition, parameter, externalParams);

            // if (checkExpr) thenExpr else true
            return Expression.Condition(checkExpr, thenExpr, Expression.Constant(true));
        }

        private static BinaryExpression MustContainIfCountIsGreater(
            Condition condition,
            Expression propertyExpression)
        {
            if (condition.Value is not JsonElement ve || ve.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Invalid value format for MustContainIfCountIsGreater.");
            }

            var target = ve.GetProperty("Target").GetString();
            var required = ve.GetProperty("Required").GetString();
            var thresholdStr = ve.GetProperty("Threshold").GetString();
            var threshold = int.Parse(thresholdStr!);

            if (target == null || required == null)
            {
                throw new InvalidOperationException("Target and Required fields are mandatory.");
            }

            // Convert value to string: x.Property.ToString()
            Expression toStringExpr = Expression.Call(propertyExpression, typeof(object).GetMethod("ToString")!);

            // Regex.Matches(valueStr, target).Count
            var regexMatchesMethod = typeof(Regex).GetMethod(nameof(Regex.Matches), new[] { typeof(string), typeof(string) })!;
            var matchesCall = Expression.Call(regexMatchesMethod, toStringExpr, Expression.Constant(target));

            var countProp = Expression.Property(matchesCall, nameof(MatchCollection.Count));
            var thresholdConstant = Expression.Constant(threshold);

            // count > threshold
            var countGreaterThanThreshold = Expression.GreaterThan(countProp, thresholdConstant);

            // valueStr.Contains(required, OrdinalIgnoreCase)
            var stringContainsMethod = typeof(string).GetMethod(
                nameof(string.Contains),
                new[] { typeof(string), typeof(StringComparison) }
            )!;

            var containsCall = Expression.Call(
                toStringExpr,
                stringContainsMethod,
                Expression.Constant(required),
                Expression.Constant(StringComparison.OrdinalIgnoreCase)
            );

            // count > threshold && valueStr.Contains(required)
            var finalExpr = Expression.AndAlso(countGreaterThanThreshold, containsCall);
            return finalExpr;
        }

        private static BinaryExpression ContainIfCountIsGreater(
            Condition condition,
            Expression propertyExpression)
        {
            if (condition.Value is not JsonElement ve || ve.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Invalid value format for ContainIfCountIsGreater.");
            }

            var target = ve.GetProperty("Target").GetString();
            var thresholdStr = ve.GetProperty("Threshold").GetString();
            var threshold = int.Parse(thresholdStr!);

            if (target == null)
            {
                throw new InvalidOperationException("Target field is mandatory.");
            }

            // Convert value to string: x.Property.ToString()
            Expression toStringExpr = Expression.Call(propertyExpression, typeof(object).GetMethod("ToString")!);

            // Regex.Matches(valueStr, target).Count
            var regexMatchesMethod = typeof(Regex).GetMethod(nameof(Regex.Matches), new[] { typeof(string), typeof(string) })!;
            var matchesCall = Expression.Call(regexMatchesMethod, toStringExpr, Expression.Constant(target));

            var countProp = Expression.Property(matchesCall, nameof(MatchCollection.Count));
            var thresholdConstant = Expression.Constant(threshold);

            // count > threshold
            var countGreaterThanThreshold = Expression.GreaterThan(countProp, thresholdConstant);

            return countGreaterThanThreshold;
        }

        private static BinaryExpression ContainIfCountIsLess(
            Condition condition,
            Expression propertyExpression)
        {
            if (condition.Value is not JsonElement ve || ve.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Invalid value format for ContainIfCountIsLess.");
            }

            var target = ve.GetProperty("Target").GetString();
            var thresholdStr = ve.GetProperty("Threshold").GetString();
            var threshold = int.Parse(thresholdStr!);

            if (target == null)
            {
                throw new InvalidOperationException("Target field is mandatory.");
            }

            // Convert value to string: x.Property.ToString()
            Expression toStringExpr = Expression.Call(propertyExpression, typeof(object).GetMethod("ToString")!);

            // Regex.Matches(valueStr, target).Count
            var regexMatchesMethod = typeof(Regex).GetMethod(nameof(Regex.Matches), new[] { typeof(string), typeof(string) })!;
            var matchesCall = Expression.Call(regexMatchesMethod, toStringExpr, Expression.Constant(target));

            var countProp = Expression.Property(matchesCall, nameof(MatchCollection.Count));
            var thresholdConstant = Expression.Constant(threshold);

            // count > threshold
            var countGreaterThanThreshold = Expression.LessThan(countProp, thresholdConstant);

            return countGreaterThanThreshold;
        }

        private static object ExtractValueWithCoercion(object rawValue, Type propertyType, string operatorName)
        {
            Type targetType = propertyType;

            if (propertyType == typeof(string) && IsNumericOperator(operatorName))
            {
                targetType = typeof(decimal);
            }

            if (rawValue is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number)
                {
                    return Convert.ChangeType(je.GetDouble(), targetType);
                }

                if (je.ValueKind == JsonValueKind.String)
                {
                    return Convert.ChangeType(je.GetString(), targetType);
                }

                if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False)
                {
                    return je.GetBoolean();
                }
            }

            return Convert.ChangeType(rawValue, targetType);
        }

        private static bool IsNumericOperator(string op) =>
            op.Equals("greaterthan", StringComparison.OrdinalIgnoreCase) ||
            op.Equals("greaterthanorequal", StringComparison.OrdinalIgnoreCase) ||
            op.Equals("lessthan", StringComparison.OrdinalIgnoreCase) ||
            op.Equals("lessthanorequal", StringComparison.OrdinalIgnoreCase);

        private static Expression ToStringExpression(Expression input)
        {
            if (input.Type == typeof(string))
                return input;

            var toStringMethod = input.Type.GetMethod("ToString", Type.EmptyTypes);
            return Expression.Call(input, toStringMethod!);
        }

    }
}

public enum SqlComparableOperator
{
    In,
    NotIn,
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

public enum MethodBasedOperator
{
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Null,
    NotNull,
    Empty,
    NotEmpty,
    NullOrEmpty,
    NotNullOrEmpty,
    MustContainIfCountIsGreater,
    ContainIfCountIsGreater,
    ContainIfCountIsLess,
    If,
    DynamicEqual,
    DynamicEmpty,
    DynamicNotEmpty,
    DynamicNullOrEmpty,
    DynamicNotNullOrEmpty,
}
