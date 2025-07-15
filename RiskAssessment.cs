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

        public async Task<string> GenerateRiskSummaryAsync(List<ShipmentRecord> shipments)
        {
            string shipmentJson = JsonConvert.SerializeObject(shipments);

            var prompt = "You are an AI assistant evaluating shipment risks.\n\n" +
                         "Analyze the following shipment records and provide a clear, multiline textual summary of the risk assessment.\n\n" +
                         "For each shipment:\n" +
                         "- Mention the upsShipmentNumber.\n" +
                         "- Indicate the RiskLevel (High, Medium, Low).\n" +
                         "- Provide the RiskReason (e.g., delay, weather, congestion, customs hold, temperature breach).\n" +
                         "- Include the RiskScore (a numeric value from 0 to 100).\n" +
                         "- Reference the LatestStatus (most recent known status or event).\n\n" +
                         "Format Requirements:\n" +
                         "- Group and summarize shipments by risk level.\n" +
                         "- For **High risk** shipments, provide detailed summaries highlighting shipment number, reason, score, and status.\n" +
                         "- For **Medium risk**, provide meaningful but concise context.\n" +
                         "- For **Low risk**, state clearly that these shipments are not currently at risk.\n" +
                         "- Use a clean, human-readable multiline format with bullet points or line breaks for clarity.\n\n" +
                         "Also, provide a **AI summary** of the overall risk analysis at the top in **markdown format**, using **bold** for key points. Be concise.\n\n" +
                         "### Shipment Records:\n" +
                         shipmentJson + "\n\n" +
                         "Provide only the textual summary, no JSON or additional notes.";

            var result = await _kernel.InvokePromptAsync(prompt);
            var summary = result.GetValue<string>()?.Trim() ?? "No summary generated.";

            return summary;
        }
    }
}
