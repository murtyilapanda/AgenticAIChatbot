using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgenticAIChatbot
{
    public class ShipmentQueryService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RaasAgenticAIFunction> _logger;

        public ShipmentQueryService(HttpClient httpClient, IConfiguration configuration, ILogger<RaasAgenticAIFunction> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<List<ShipmentRecord>> QueryShipmentsAsync(Dictionary<string, string> filters)
        {
            if (filters == null || filters.Count == 0)
                throw new ArgumentException("You must specify at least one filter.");

            bool useAnd = filters.Count == 1;

            string query = CosmosQueryHelper.BuildSqlQuery(filters, useAnd); // OR condition
            var payload = new
            {
                status = "dynamic",
                query = query
            };

            var apiUrl = _configuration["ShipmentApiUrl"];
            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(apiUrl, content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();

            var shipmentResponse = JsonConvert.DeserializeObject<ShipmentResponse>(json);

            if (shipmentResponse?.ShipmentList == null || !shipmentResponse.ShipmentList.Any())
            {
                _logger.LogWarning("No shipments found in response.");
                return new List<ShipmentRecord>();
            }

            return shipmentResponse.ShipmentList;
        }

        public class ShipmentResponse
        {
            public List<ShipmentRecord> ShipmentList { get; set; } = new();
            public bool Success { get; set; }
        }

    }

}
