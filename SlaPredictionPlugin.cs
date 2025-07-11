using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AgenticAIChatbot;

public class SlaPredictionPlugin
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly bool _useMockPredictions;
    private readonly string _mockPredictionsPath;

    public SlaPredictionPlugin(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["SlaPredictionPlugin:ApiKey"];
        _endpoint = configuration["SlaPredictionPlugin:Endpoint"];
        _mockPredictionsPath = configuration["SlaPredictionPlugin:MockPredictionsPath"];
        _useMockPredictions = bool.TryParse(configuration["SlaPredictionPlugin:UseMockPredictions"], out var val) && val;
    }

    [KernelFunction]
    public async Task<string> PredictSlaAsync(string shipmentsJson)
    {
        try
        {
            JsonNode shipmentsNode = JsonNode.Parse(shipmentsJson);
            var inputShipments = shipmentsNode["shipmentList"]?.AsArray();
            if (inputShipments == null)
                throw new InvalidOperationException("Input JSON must contain a 'shipmentList' array property.");

            if (_useMockPredictions)
                await ApplyMockPredictions(inputShipments);
            else
                await ApplyMlPredictions(inputShipments);

            return shipmentsNode.ToJsonString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in SLA prediction: {ex.Message}");

            if (!_useMockPredictions)
            {
                Console.WriteLine("Falling back to mock predictions...");
                try
                {
                    var inputShipments = JsonNode.Parse(shipmentsJson)["shipmentList"]?.AsArray();
                    if (inputShipments != null)
                    {
                        await ApplyMockPredictions(inputShipments);
                        return JsonNode.Parse(shipmentsJson).ToJsonString();
                    }
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteLine($"Fallback error: {fallbackEx.Message}");
                }
            }
            throw;
        }
    }

    private async Task ApplyMlPredictions(JsonArray inputShipments)
    {
        var mlShipments = new JsonArray();
        foreach (var shipment in inputShipments)
        {
            var shipmentObject = shipment.AsObject();
            DateTime? creationDateTime = ParseDateTime(shipmentObject["shipmentCreationDatetime"]?.ToString());
            DateTime? pickupDateTime = ParseDateTime(shipmentObject["pickupDatetime"]?.ToString());
            DateTime? etaDateTime = ParseDateTime(shipmentObject["deliveryETADatetime"]?.ToString());

            var mlShipment = new JsonObject
            {
                ["shipmentMode"] = shipmentObject["shipmentMode"]?.ToString(),
                ["carrierService"] = shipmentObject["carrierService"]?.ToString(),
                ["originCity"] = shipmentObject["originCity"]?.ToString(),
                ["destinationCity"] = shipmentObject["destinationCity"]?.ToString(),
                ["originCountry"] = shipmentObject["originCountry"]?.ToString(),
                ["destinationCountry"] = shipmentObject["destinationCountry"]?.ToString(),
                ["creation_hour"] = creationDateTime?.Hour,
                ["pickup_hour"] = pickupDateTime?.Hour,
                ["eta_hour"] = etaDateTime?.Hour,
                ["days_to_pickup"] = pickupDateTime.HasValue && creationDateTime.HasValue ? (int?)(pickupDateTime.Value - creationDateTime.Value).TotalDays : null,
                ["days_to_eta"] = etaDateTime.HasValue && creationDateTime.HasValue ? (int?)(etaDateTime.Value - creationDateTime.Value).TotalDays : null,
                ["airRisk"] = ExtractRiskScore(shipmentObject, "Air"),
                ["oceanRisk"] = ExtractRiskScore(shipmentObject, "Ocean"),
                ["surfaceRisk"] = ExtractRiskScore(shipmentObject, "Surface")
            };
            mlShipments.Add(mlShipment);
        }

        var payload = new { data = mlShipments };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        var response = await _httpClient.PostAsJsonAsync(_endpoint, payload);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        var predictionResult = JsonNode.Parse(responseContent);

        if (predictionResult?["prediction"] is JsonArray predictionArray && predictionResult["probability"] is JsonArray probabilityArray)
        {
            for (int i = 0; i < inputShipments.Count && i < predictionArray.Count; i++)
            {
                var shipment = inputShipments[i].AsObject();
                bool slaBreach = TryParseBoolean(predictionArray[i]);
                double? probability = TryParseDouble(probabilityArray[i]);

                shipment["slaBreach"] = slaBreach;
                shipment["slaBreachProbability"] = probability;
            }
        }
    }

    private async Task ApplyMockPredictions(JsonArray inputShipments)
    {
        JsonNode mockPredictions = await LoadMockPredictions();
        if (mockPredictions == null) return;

        var predArray = mockPredictions["prediction"] as JsonArray;
        var probArray = mockPredictions["probability"] as JsonArray;

        for (int i = 0; i < inputShipments.Count; i++)
        {
            var shipment = inputShipments[i].AsObject();
            int mockIndex = i % (predArray?.Count ?? 1);

            bool slaBreach = TryParseBoolean(predArray?[mockIndex]);
            double? probability = TryParseDouble(probArray?[mockIndex]);

            shipment["slaBreach"] = slaBreach;
            shipment["slaBreachProbability"] = probability;
        }
    }

    private async Task<JsonNode?> LoadMockPredictions()
    {
        if (!File.Exists(_mockPredictionsPath)) return null;
        string mockJson = await File.ReadAllTextAsync(_mockPredictionsPath);
        return JsonNode.Parse(mockJson);
    }

    private bool TryParseBoolean(JsonNode? node)
    {
        if (node == null) return false;
        if (node is JsonValue value)
        {
            return value.TryGetValue(out bool boolVal) ? boolVal :
                   value.TryGetValue(out string strVal) ? strVal.Equals("true", StringComparison.OrdinalIgnoreCase) || strVal == "1" :
                   value.TryGetValue(out double dblVal) && dblVal > 0.5;
        }
        return false;
    }

    private double? TryParseDouble(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out double result))
        {
            return result;
        }
        return null;
    }

    private DateTime? ParseDateTime(string? dateTimeStr)
    {
        return DateTime.TryParse(dateTimeStr, out DateTime dt) ? dt : null;
    }

    private int? ExtractRiskScore(JsonObject shipment, string mode)
    {
        return mode switch
        {
            "Air" => int.TryParse(shipment["airRisk"]?.ToString(), out var air) ? air : null,
            "Ocean" => int.TryParse(shipment["oceanRisk"]?.ToString(), out var ocean) ? ocean : null,
            "Surface" => int.TryParse(shipment["surfaceRisk"]?.ToString(), out var surface) ? surface : null,
            _ => shipment["shipmentMode"]?.GetValue<string>() == mode && int.TryParse(shipment["portCongestionRiskScore"]?.ToString(), out var portRisk) ? portRisk : (int?)5
        };
    }
}