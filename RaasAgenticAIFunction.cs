using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Newtonsoft.Json;

namespace AgenticAIChatbot
{
    public class RaasAgenticAIFunction
    {
        private readonly ILogger<RaasAgenticAIFunction> _logger;
        private readonly Container _container;
        private readonly Kernel _kernel;
        private readonly RiskAssessment _riskAssessment;

        public RaasAgenticAIFunction(ILogger<RaasAgenticAIFunction> logger, Container container, Kernel kernel, RiskAssessment riskAssessment)
        {
            _logger = logger;
            _container = container;
            _kernel = kernel;
            _riskAssessment = riskAssessment;
        }

        [Function("RaasAgenticAIFunction")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req)
        {
            try
            {
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var data = JsonConvert.DeserializeObject<Queries>(requestBody);

                if (data?.message == null)
                {
                    return new BadRequestObjectResult("Missing or invalid 'message' in request.");
                }

             var prompt = "You are an AI assistant that extracts structured filter criteria from user queries related to shipment data.\n\n" +
             "### Objective:\n" +
             "Extract all relevant filters present in the user message and return them as a JSON dictionary. Use only the valid field names listed below as keys.\n\n" +
             "### Valid Field Names:\n" +
             "upsShipmentNumber, shipmentMode, carrierService, shipmentCreationDatetime, pickupDatetime, deliveryETADatetime, actualDeliveryDatetime,\n" +
             "milestoneStatus, deliveryStatus, isAtRisk, atRiskSeverity, weatherMetar, weatherCondition, trafficCondition,\n" +
             "originPortCode, destinationPortCode, flightIATA, containerNumber, originCity, destinationCity,\n" +
             "originCountry, destinationCountry, weatherConditionRiskScore, trafficConditionRiskScore,\n" +
             "portCongestionRiskScore, airportCongestionRiskScore, flightDelayRiskScore, airRisk, oceanRisk, surfaceRisk\n\n" +
             "### Interpretation Rules:\n" +
             "- If the message says \"to [city]\" or \"delivered in [city]\", use `destinationCity`.\n" +
             "- If the message says \"from [city]\", use `originCity`.\n" +
             "- If a number is mentioned and it matches known formats (e.g., UPS tracking numbers, flight numbers), infer the most likely field:\n" +
             "    - If explicitly called \"shipment number\" → `upsShipmentNumber`\n" +
             "    - If called \"container number\" → `containerNumber`\n" +
             "    - If called \"flight number\" or starts with two letters and digits (e.g., \"AA123\") → `flightIATA`\n" +
             "- Do not guess unknown fields; include only those you are confident about.\n\n" +
             "If you think it might be any of the following like RiskScore you can include all the risk score fields or if its any number some of the numbers fields it could be more than 1, 2, 3, 4 fields we need to get the fields and value\n\n" +
             $"### Input Message:\n\"{data.message}\"\n\n" +
             "Return only a JSON dictionary. Example:\n{\"destinationCity\": \"xyz\", \"originCity\": \"abc\", \"containerNumber\": \"123\"} ";

                var skResult = await _kernel.InvokePromptAsync(prompt);
                Dictionary<string, string> filters = new();

                try
                {
                    var jsonString = skResult?.GetValue<string>()?.Trim() ?? "{}";

                    // Optional cleanup if it contains markdown like ```json
                    if (jsonString.StartsWith("```"))
                    {
                        int start = jsonString.IndexOf('{');
                        int end = jsonString.LastIndexOf('}');
                        if (start >= 0 && end > start)
                        {
                            jsonString = jsonString.Substring(start, end - start + 1);
                        }
                    }

                    filters = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonString) ?? new Dictionary<string, string>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to parse AI result: " + ex.Message);
                    filters = new Dictionary<string, string>();
                }

                bool queryCondition = false;

                if (filters.Count() == 1)
                {
                    queryCondition = true;
                }

                if (filters == null || filters.Count == 0)
                {
                    return new BadRequestObjectResult("You must specify at least one filter.");
                }

                var queryDef = CosmosQueryHelper.BuildQuery(filters, queryCondition);

                var results = new List<ShipmentRecord>();
                using var feed = _container.GetItemQueryIterator<dynamic>(queryDef);
                while (feed.HasMoreResults)
                {
                    var response = await feed.ReadNextAsync();
                    foreach (var item in response)
                    {
                        var record = JsonConvert.DeserializeObject<ShipmentRecord>(item.ToString());
                        if (record != null)
                        {
                            results.Add(record);
                        }
                    }
                }

                var riskSummary = await _riskAssessment.GenerateRiskSummaryAsync(results);

                _logger.LogInformation("Risk summary: {RiskSummary}", JsonConvert.SerializeObject(riskSummary, Formatting.Indented));

                // ✅ Return response with both data and risk assessment
                return new OkObjectResult(new
                {
                    message = $"Fetched {results.Count} shipment record(s)",
                    shipments = results,
                    riskAssessment = riskSummary
                });
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSON deserialization failed.");
                return new BadRequestObjectResult("Invalid JSON in request body.");
            }
            catch (CosmosException cosmosEx)
            {
                _logger.LogError(cosmosEx, "Cosmos DB query failed.");
                return new ObjectResult("Database error occurred.")
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception occurred.");
                return new ObjectResult("An unexpected error occurred.")
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }
    }

    public class Queries
    {
        public string? message { get; set; }
    }


    public class ShipmentRecord
    {
        public string? id { get; set; }
        public string? upsShipmentNumber { get; set; }
        public string? shipmentMode { get; set; }
        public string? carrierService { get; set; }
        public string? shipmentCreationDatetime { get; set; }
        public string? pickupDatetime { get; set; }
        public string? deliveryETADatetime { get; set; }
        public string? actualDeliveryDatetime { get; set; }
        public string? milestoneStatus { get; set; }
        public string? deliveryStatus { get; set; }
        public string? isAtRisk { get; set; }
        public string? atRiskSeverity { get; set; }
        public string? weatherMetar { get; set; }
        public string? weatherCondition { get; set; }
        public string? trafficCondition { get; set; }
        public string? originPortCode { get; set; }
        public string? destinationPortCode { get; set; }
        public string? flightIATA { get; set; }
        public string? containerNumber { get; set; }
        public string? originCity { get; set; }
        public string? destinationCity { get; set; }
        public string? originCountry { get; set; }
        public string? destinationCountry { get; set; }
        public string? weatherConditionRiskScore { get; set; }
        public string? trafficConditionRiskScore { get; set; }
        public string? portCongestionRiskScore { get; set; }
        public string? airportCongestionRiskScore { get; set; }
        public string? flightDelayRiskScore { get; set; }
        public string? airRisk { get; set; }
        public string? oceanRisk { get; set; }
        public string? surfaceRisk { get; set; }
    }

    public class ShipmentRisk
    {
        public string? upsShipmentNumber { get; set; }
        public string? RiskLevel { get; set; }
        public string? RiskReason { get; set; }
    }


}
