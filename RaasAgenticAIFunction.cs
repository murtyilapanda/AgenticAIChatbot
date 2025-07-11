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
        private readonly ShipmentQueryService _shipmentQueryService;
        private readonly SLAQueryService _slaQueryService;
        private readonly SlaPredictionPlugin _slaPredictionPlugin;
        private readonly CosmosDbPlugin _cosmosDbPlugin;

        public RaasAgenticAIFunction(ILogger<RaasAgenticAIFunction> logger, Container container, Kernel kernel, 
                                     RiskAssessment riskAssessment, ShipmentQueryService shipmentQueryService, 
                                     SLAQueryService slaQueryService, SlaPredictionPlugin slaPredictionPlugin,
                                     CosmosDbPlugin cosmosDbPlugin)
        {
            _logger = logger;
            _container = container;
            _kernel = kernel;
            _riskAssessment = riskAssessment;
            _shipmentQueryService = shipmentQueryService;
            _slaQueryService = slaQueryService;
            _slaPredictionPlugin = slaPredictionPlugin;
            _cosmosDbPlugin = cosmosDbPlugin;
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

                string classifyAndExtractPrompt = "You are a smart assistant helping to classify user shipment-related queries.\n\n" +
                                                  "## Objective:\n" +
                                                  "Determine the type of request from the message and respond with one of the following string values:\n" +
                                                  "- \"shipment\" → if the user asks for shipment details (e.g., origin, destination, tracking, delivery)\n" +
                                                  "- \"sla\" → if the user asks about SLA status, SLA breaches, or missed SLA\n" +
                                                  "- \"general\" → if the user message is a greeting, help request, or unrelated\n\n" +
                                                  "Your task is to respond with only one of the following words, based on the user's message:\n" +
                                                  "- shipment\n" +
                                                  "- sla\n" +
                                                  "- general\n\n" +
                                                  $"### Input Message:\n\"{data?.message}\"\n\n" +
                                                  "Do not explain, repeat, or include any other words. Just reply with one word only: shipment, sla, or general.";

                var switchItem = await _kernel.InvokePromptAsync(classifyAndExtractPrompt);

                var classification = switchItem?.GetValue<string>()?.Trim().ToLower() ?? "general";

                switch (classification)
                {
                    case "sla":
                        try
                        {
                            _kernel.ImportPluginFromObject(_cosmosDbPlugin, "Shipments");
                            _kernel.ImportPluginFromObject(_slaPredictionPlugin, "Predictor");
                            var summary = await _slaQueryService.GetShipmentSummaryAsync(data?.message);
                            return new OkObjectResult(new
                            {
                                message = "SLA summary generated successfully.",
                                summary = summary
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing SLA summary");
                            return new ObjectResult("Unable to generate SLA summary.")
                            {
                                StatusCode = StatusCodes.Status500InternalServerError
                            };
                        }

                    case "shipment":
                        {
                            string prompt = "You are an AI assistant that extracts structured filter criteria from user queries related to shipment data.\n\n" +
                                "### Objective:\n" +
                                "Extract all relevant filters present in the user message and return them as a JSON dictionary. Use only the valid field names listed below as keys.\n\n" +
                                "### Valid Field Names:\n" +
                                "upsShipmentNumber, shipmentMode, carrierService, shipmentCreationDatetime, pickupDatetime, deliveryETADatetime, actualDeliveryDatetime,\n" +
                                "milestoneStatus, deliveryStatus, isAtRisk, atRiskSeverity, weatherMetar, weatherCondition, trafficCondition,\n" +
                                "originPortCode, destinationPortCode, flightIATA, containerNumber, originCity, destinationCity,\n" +
                                "originCountry, destinationCountry, weatherConditionRiskScore, trafficConditionRiskScore,\n" +
                                "portCongestionRiskScore, airportCongestionRiskScore, flightDelayRiskScore, airRisk, oceanRisk, surfaceRisk,\n" +
                                "potentialWeatherConditionRiskScore, potentialTrafficConditionRiskScore,\n" +
                                "potentialPortCongestionRiskScore, potentialAirportCongestionRiskScore, potentialFlightDelayRiskScore\n" +
                                "accountNumber, carrierShipmentNumber, totalCharge, invoiceDate, zone, proofOfDelivery, lastKnownLocation,\n" +
                                "shipperName, shipperAddress, receiverName, receiverAddress, serviceType, packageWeight, dimensions,\n" +
                                "documents, originCoordinates, destinationCoordinates, trackingHistory\n\n" +
                                "- RiskLevel (High, Medium, Low)\n" +
                                "- RiskReason (e.g., delay, weather, congestion)\n\n" +
                                "If the user mentions **high/medium/low risk** (for weather, traffic, delays, etc.), map these qualitative levels to numeric scores:\n" +
                                "- Low → 1\n" +
                                "- Medium → 3\n" +
                                "- High → 5\n" +
                                "Evaluate all fields ending with `RiskScore` or `potential*RiskScore` accordingly.\n\n" +
                                "Add when you think it could have multiple fields could match with prompt give all the fields\n\n" +
                                "### Interpretation Rules:\n" +
                                "- If the message says \"to [city]\" or \"delivered in [city]\", use `destinationCity`.\n" +
                                "- If the message says \"from [city]\", use `originCity`.\n" +
                                "- If a number is mentioned and it matches known formats (e.g., UPS tracking numbers, flight numbers), infer the most likely field:\n" +
                                "    - If explicitly called \"shipment number\" → `upsShipmentNumber`\n" +
                                "    - If called \"container number\" → `containerNumber`\n" +
                                "    - If called \"flight number\" or starts with two letters and digits (e.g., \"AA123\") → `flightIATA`\n" +
                                "- Do not guess unknown fields; include only those you are confident about.\n\n" +
                                "If you think it might be any of the following like RiskScore you can include all the risk score fields or if its any number some of the numbers fields it could be more than 1, 2, 3, 4 fields we need to get the fields and value\n\n" +
                                $"### Input Message:\n\"{data?.message}\"\n\n" +
                                "Return only a JSON dictionary. Example:\n{\"destinationCity\": \"xyz\", \"originCity\": \"abc\", \"containerNumber\": \"123\",  \"potentialWeatherConditionRiskScore\": \"5\"}";

                            var skResult = await _kernel.InvokePromptAsync(prompt);

                            Dictionary<string, string> filters = new();

                            try
                            {
                                var jsonString = skResult?.GetValue<string>()?.Trim() ?? "{}";

                                if (jsonString.StartsWith("```"))
                                {
                                    int start = jsonString.IndexOf('{');
                                    int end = jsonString.LastIndexOf('}');
                                    if (start >= 0 && end > start)
                                    {
                                        jsonString = jsonString.Substring(start, end - start + 1);
                                    }
                                }

                                filters = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonString) ?? new();
                                _logger.LogInformation("Deserialized filters: {FiltersJson}", JsonConvert.SerializeObject(filters, Formatting.Indented));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to parse filter JSON from AI response");
                                return new BadRequestObjectResult("Failed to extract shipment filters from input.");
                            }

                            List<ShipmentRecord> shipments;
                            try
                            {
                                shipments = await _shipmentQueryService.QueryShipmentsAsync(filters);
                            }
                            catch (ArgumentException ex)
                            {
                                return new BadRequestObjectResult(ex.Message);
                            }

                            var riskSummary = await _riskAssessment.GenerateRiskSummaryAsync(shipments);

                            _logger.LogInformation("Risk summary: {RiskSummary}", JsonConvert.SerializeObject(riskSummary, Formatting.Indented));

                            return new OkObjectResult(new
                            {
                                message = $"Fetched {shipments.Count} shipment record(s)",
                                shipments = shipments,
                                riskAssessment = riskSummary
                            });
                        }

                    default:
                        return new OkObjectResult(new
                        {
                            message = "Hi! I can help you with shipment details or SLA-related queries. Try asking something like:\n" +
                                      "- 'Show me delayed shipments to New York'\n" +
                                      "- 'Which shipments might miss SLA this week?'"
                        });

                }

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
        public string? potentialWeatherConditionRiskScore { get; set; }
        public string? potentialTrafficConditionRiskScore { get; set; }
        public string? potentialPortCongestionRiskScore { get; set; }
        public string? potentialAirportCongestionRiskScore { get; set; }
        public string? potentialFlightDelayRiskScore { get; set; }
        public string? accountNumber { get; set; }
        public string? carrierShipmentNumber { get; set; }
        public string? totalCharge { get; set; }
        public string? invoiceDate { get; set; }
        public string? zone { get; set; }
        public string? proofOfDelivery { get; set; }
        public string? lastKnownLocation { get; set; }
        public string? shipperName { get; set; }
        public string? shipperAddress { get; set; }
        public string? receiverName { get; set; }
        public string? receiverAddress { get; set; }
        public string? serviceType { get; set; }
        public string? packageWeight { get; set; }
        public string? dimensions { get; set; }
        public string? documents { get; set; }
        public string? originCoordinates { get; set; }
        public string? destinationCoordinates { get; set; }
        public string? trackingHistory { get; set; }
    }


    public class ShipmentRisk
    {
        public string? upsShipmentNumber { get; set; }
        public string? RiskLevel { get; set; }
        public string? RiskReason { get; set; }
    }

    public class FilterCriteria
    {
        public string? ShipmentMode { get; set; }
        public string? OriginCity { get; set; }
        public string? DestinationCity { get; set; }
        public bool AtRisk { get; set; }
        public string? ShipmentCreationDateTime { get; set; }
        public string? DeliveryETADateTime { get; set; }
    }

}
