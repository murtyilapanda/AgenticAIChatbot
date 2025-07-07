using Microsoft.SemanticKernel;
using Newtonsoft.Json;

namespace AgenticAIChatbot
{
    public class RiskAssessment
    {
        private readonly Kernel _kernel;

        public RiskAssessment(Kernel kernel)
        {
            _kernel = kernel;
        }

        public async Task<List<ShipmentRisk>> GenerateRiskSummaryAsync(List<ShipmentRecord> shipments)
        {
            string shipmentJson = JsonConvert.SerializeObject(shipments);

            var prompt =
                "You are an AI assistant evaluating shipment risks.\n\n" +
                "Analyze the shipment records and return a JSON array with each shipment's:\n" +
                "- upsShipmentNumber\n" +
                "- RiskLevel (High, Medium, Low)\n" +
                "- RiskReason (e.g., delay, weather, congestion)\n\n" +
                "Respond only with JSON array.\n\n" +
                "### Shipment Records:\n" +
                shipmentJson + "\n\n" +
                "Respond with only valid JSON.";

            var result = await _kernel.InvokePromptAsync(prompt);
            var json = result.GetValue<string>() ?? "[]";

            return JsonConvert.DeserializeObject<List<ShipmentRisk>>(json) ?? new();
        }
    }
}
