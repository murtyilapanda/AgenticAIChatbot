using Microsoft.Azure.Cosmos;

namespace AgenticAIChatbot
{
    public static class CosmosQueryHelper
    {
        /// <summary>
        /// Dynamically builds a parameterized SQL query for Cosmos DB based on key-value filters.
        /// </summary>
        /// <param name="filters">A dictionary of field names and values to use in WHERE clause.</param>
        /// <param name="useAnd">Optional: Use AND (true) or OR (false) to combine conditions.</param>
        /// <returns>A QueryDefinition object ready for Cosmos DB queries.</returns>
        public static QueryDefinition BuildQuery(Dictionary<string, string> filters, bool useAnd = true)
        {
            string baseSql = "SELECT * FROM c";
            string whereClause = string.Empty;

            if (filters != null && filters.Any())
            {
                var conditions = new List<string>();
                foreach (var kvp in filters)
                {
                    var paramName = $"@{kvp.Key}";
                    conditions.Add($"c.{kvp.Key} = {paramName}");
                }

                var joiner = useAnd ? " AND " : " OR ";
                whereClause = " WHERE " + string.Join(joiner, conditions);
            }

            string fullQuery = baseSql + whereClause;
            var queryDef = new QueryDefinition(fullQuery);

            // Bind parameters
            foreach (var kvp in filters)
            {
                queryDef.WithParameter($"@{kvp.Key}", kvp.Value);
            }

            return queryDef;
        }
    }

}
