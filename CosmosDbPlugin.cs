using Microsoft.SemanticKernel;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticAIChatbot;

public class CosmosDbPlugin
{
    private readonly HttpClient _httpClient;
    private readonly string _getShipmentsEndpoint;

    public CosmosDbPlugin(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _getShipmentsEndpoint = configuration["ShipmentApiUrl"]
            ?? throw new InvalidOperationException("GetShipments endpoint is not configured.");
    }

    [KernelFunction]
    public async Task<string> GetShipmentsDueThisWeekAsync()
    {
        var content = new StringContent("{\"status\":\"all\"}", Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_getShipmentsEndpoint, content);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    [KernelFunction]
    public async Task<string> GetFilteredShipmentsAsync(
        string shipmentMode = null,
        string originCity = null,
        string destinationCity = null,
        string atRisk = null,
        string shipmentCreationDateTime = null,
        string deliveryETADateTime = null)
    {
        var initialContent = new StringContent("{\"status\":\"all\"}", Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(_getShipmentsEndpoint, initialContent);
        response.EnsureSuccessStatusCode();

        var shipmentsJson = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(shipmentsJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("shipmentList", out var shipmentList) || shipmentList.ValueKind != JsonValueKind.Array)
            return "{\"shipmentList\":[]}";

        var filteredShipments = new JsonArray();

        bool? atRiskBool = bool.TryParse(atRisk, out var parsed) ? parsed : null;

        (DateTime? creationStart, DateTime? creationEnd) = ParseTimeFrame(shipmentCreationDateTime);
        (DateTime? deliveryStart, DateTime? deliveryEnd) = ParseTimeFrame(deliveryETADateTime);

        foreach (var shipment in shipmentList.EnumerateArray())
        {
            bool include = true;

            if (!string.IsNullOrEmpty(shipmentMode) &&
                shipment.TryGetProperty("shipmentMode", out var mode) &&
                mode.ValueKind == JsonValueKind.String)
                include &= mode.GetString().Equals(shipmentMode, StringComparison.OrdinalIgnoreCase);

            if (include && !string.IsNullOrEmpty(originCity) &&
                shipment.TryGetProperty("originCity", out var origin) &&
                origin.ValueKind == JsonValueKind.String)
                include &= origin.GetString().Contains(originCity, StringComparison.OrdinalIgnoreCase);

            if (include && !string.IsNullOrEmpty(destinationCity) &&
                shipment.TryGetProperty("destinationCity", out var dest) &&
                dest.ValueKind == JsonValueKind.String)
                include &= dest.GetString().Contains(destinationCity, StringComparison.OrdinalIgnoreCase);

            if (include && creationStart.HasValue && creationEnd.HasValue &&
                shipment.TryGetProperty("shipmentCreationDatetime", out var creationProp) &&
                creationProp.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(creationProp.GetString(), out var creationDate))
                include &= creationDate >= creationStart && creationDate <= creationEnd;

            if (include && deliveryStart.HasValue && deliveryEnd.HasValue &&
                shipment.TryGetProperty("deliveryETADatetime", out var deliveryProp) &&
                deliveryProp.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(deliveryProp.GetString(), out var deliveryDate))
                include &= deliveryDate >= deliveryStart && deliveryDate <= deliveryEnd;

            if (include)
                filteredShipments.Add(JsonSerializer.Deserialize<JsonObject>(shipment.GetRawText()));
        }

        return new JsonObject { ["shipmentList"] = filteredShipments }.ToJsonString();
    }

    private (DateTime? Start, DateTime? End) ParseTimeFrame(string frame)
    {
        if (string.IsNullOrWhiteSpace(frame)) return (null, null);

        var now = DateTime.Now.Date;
        if (frame.Contains("today", StringComparison.OrdinalIgnoreCase)) return (now, now.AddDays(1).AddSeconds(-1));
        if (frame.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)) return (now.AddDays(1), now.AddDays(2).AddSeconds(-1));
        if (frame.Contains("yesterday", StringComparison.OrdinalIgnoreCase)) return (now.AddDays(-1), now.AddSeconds(-1));
        if (frame.Contains("this week", StringComparison.OrdinalIgnoreCase))
        {
            var start = now.AddDays(-(int)now.DayOfWeek);
            return (start, start.AddDays(7).AddSeconds(-1));
        }
        if (frame.Contains("next week", StringComparison.OrdinalIgnoreCase))
        {
            var start = now.AddDays(7 - (int)now.DayOfWeek);
            return (start, start.AddDays(7).AddSeconds(-1));
        }
        if (frame.Contains("this month", StringComparison.OrdinalIgnoreCase))
        {
            var start = new DateTime(now.Year, now.Month, 1);
            return (start, start.AddMonths(1).AddSeconds(-1));
        }
        if (frame.Contains("next month", StringComparison.OrdinalIgnoreCase))
        {
            var start = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            return (start, start.AddMonths(1).AddSeconds(-1));
        }

        return (null, null);
    }
}
