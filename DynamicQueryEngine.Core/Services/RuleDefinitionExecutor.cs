using DynamicQueryEngine.Core.Models;
using DynamicQueryEngine.Core.Services;

namespace TK.MOS.Domain.DynamicQueryEngine.Services
{
    public static class RuleDefinitionExecutor<T>
    {
        public static IEnumerable<T> Executes(
            IEnumerable<T> value,
            IEnumerable<RuleDefinition> rules,
            IDictionary<string, object>? externalParams = null)
        {
            IEnumerable<T> result = Enumerable.Empty<T>();

            foreach (var rule in rules)
            {
                var ruledValues = Execute(
                    value,
                    rule,
                    externalParams);

                result = result.Concat(ruledValues);
            }

            HashSet<T> hashed = new HashSet<T>(result);

            return hashed;
        }

        public static IEnumerable<T> Execute(
            IEnumerable<T> value,
            RuleDefinition rule,
            IDictionary<string, object>? externalParams = null)
        {
            return value
                .AsQueryable()
                .ApplyRule(rule, externalParams)
                .Select(x => (T)x)
                .ToList();
        }
    }
}
