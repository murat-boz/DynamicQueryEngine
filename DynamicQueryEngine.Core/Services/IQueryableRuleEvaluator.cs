using DynamicQueryEngine.Core.Models;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace DynamicQueryEngine.Core.Services
{
    public static class IQueryableRuleEvaluator
    {
        //public static IEnumerable<T> ApplyRule<T>(this IEnumerable<T> source, RuleDefinition rule)
        //{
        //    RuleValidator.Validate<T>(rule);

        //    IEnumerable<T> filtered = source;

        //    // Filter Expression
        //    var parameter = Expression.Parameter(typeof(T), "x");

        //    // Condition varsa filtre uygula
        //    if (rule.Conditions != null
        //        && (rule.Conditions.Conditions.Any() || rule.Conditions.Groups.Any()))
        //    {
        //        var filter = BuildFilter<T>(rule.Conditions, parameter).Compile();
        //        filtered = filtered.Where(filter);
        //    }

        //    // Eğer GroupBy veya Aggregate yoksa filtre sonucu döndür
        //    if (rule.GroupBy == null || rule.GroupBy.Count == 0 || rule.Aggregation == null)
        //    {
        //        return filtered;
        //    }

        //    // GroupBy selector
        //    var groupByProp = Expression.Property(parameter, rule.GroupBy.First());
        //    var groupBySelector = Expression.Lambda<Func<T, object>>(
        //        Expression.Convert(groupByProp, typeof(object)), parameter);

        //    // Aggregate selector
        //    var aggregateProp = Expression.Property(parameter, rule.Aggregation.AggregateProperty);
        //    var aggregateSelector = Expression.Lambda<Func<T, object>>(
        //        Expression.Convert(aggregateProp, typeof(object)), parameter);

        //    // IQueryable dynamic OrderBy / OrderByDescending
        //    return AggregationEngine.ApplyAggregation(filtered, rule);
        //}

        public static IQueryable<object> ApplyRule<T>(this IQueryable<T> source, RuleDefinition rule)
        {
            RuleValidator.Validate<T>(rule);

            IQueryable<T> filtered = source;

            // Filter Expression
            var parameter = Expression.Parameter(typeof(T), "x");

            // Condition varsa filtre uygula
            if (rule.Conditions != null &&
                (rule.Conditions.Conditions.Any() || rule.Conditions.Groups.Any()))
            {
                var filter = BuildFilter<T>(rule.Conditions, parameter);
                filtered = filtered.Where(filter);
            }

            // Eğer GroupBy veya Aggregate yoksa filtre sonucu döndür
            if (rule.GroupBy == null || rule.GroupBy.Count == 0 || rule.Aggregation == null)
            {
                return filtered.Cast<object>();
            }

            // Eğer AggregateFunction Count ise projection yap
            if (rule.Aggregation.AggregateFunction == AggregateFunction.Count)
            {
                var groupBySelector = BuildGroupBySelector<T>(rule.GroupBy, parameter);
                var grouped = filtered.GroupBy(groupBySelector);

                var projected = grouped.Select(g => new
                {
                    GroupKey = g.Key,
                    Count = g.Count()
                });

                return projected.Cast<object>();
            }

            // GroupBy selector
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
            Expression<Func<T, object>>? aggregateSelector,
            AggregationDefinition aggregation)
        {
            switch (aggregation.AggregateFunction)
            {
                case AggregateFunction.Min:
                    return group.OrderBy(aggregateSelector).First();

                case AggregateFunction.Max:
                    return group.OrderByDescending(aggregateSelector).First();

                //case AggregateFunction.Count:
                //    return group.Count();

                default:
                    throw new NotSupportedException($"Aggregate function '{aggregation.AggregateFunction}' not supported.");
            }
        }


        // Build dynamic group key for Count aggregation
        private static Expression<Func<T, object>> BuildGroupBySelector<T>(List<string> groupByProperties, ParameterExpression parameter)
        {
            var anonType = DynamicAnonymousTypeFactory.CreateAnonymousType(groupByProperties, typeof(T));

            var bindings = groupByProperties.Select(propName =>
            {
                var propInfo = typeof(T).GetProperty(propName, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                var propertyExpr = Expression.Property(parameter, propInfo);
                var anonProp = anonType.GetProperty(propName);
                return Expression.Bind(anonProp, propertyExpr);
            }).ToList();

            var body = Expression.MemberInit(Expression.New(anonType), bindings);
            return Expression.Lambda<Func<T, object>>(Expression.Convert(body, typeof(object)), parameter);
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
            {
                expressions.Add(BuildCondition<T>(condition, parameter));
            }

            foreach (var subgroup in group.Groups)
            {
                expressions.Add(BuildGroupBody<T>(subgroup, parameter));
            }

            return group.LogicalOperator.ToUpperInvariant() == "OR" 
                ? expressions.Aggregate(Expression.OrElse) 
                : expressions.Aggregate(Expression.AndAlso);
        }

        private static Expression BuildCondition<T>(Condition condition, ParameterExpression parameter)
        {
            var prop = typeof(T).GetProperty(condition.Property, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            Expression propertyAccess = Expression.Property(parameter, prop);

            if (prop.PropertyType == typeof(string) && 
                IsNumericOperator(condition.Operator))
            {
                var parseMethod = typeof(decimal).GetMethod(nameof(decimal.Parse), new[] { typeof(string) });
                propertyAccess = Expression.Call(parseMethod, propertyAccess);
            }

            var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            var op = condition.Operator.ToLowerInvariant();

            if (op == "in")
            {
                if (condition.Value is JsonElement je && 
                    je.ValueKind == JsonValueKind.Array)
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
                    "equal"              => Expression.Equal(propertyAccess, constant),
                    "notequal"           => Expression.NotEqual(propertyAccess, constant),
                    "greaterthan"        => Expression.GreaterThan(propertyAccess, constant),
                    "greaterthanorequal" => Expression.GreaterThanOrEqual(propertyAccess, constant),
                    "lessthan"           => Expression.LessThan(propertyAccess, constant),
                    "lessthanorequal"    => Expression.LessThanOrEqual(propertyAccess, constant),
                    _                    => throw new NotSupportedException()
                };
            }
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
            op.Equals("greaterthan", StringComparison.OrdinalIgnoreCase)        || 
            op.Equals("greaterthanorequal", StringComparison.OrdinalIgnoreCase) ||
            op.Equals("lessthan", StringComparison.OrdinalIgnoreCase)           || 
            op.Equals("lessthanorequal", StringComparison.OrdinalIgnoreCase);
    }
}
