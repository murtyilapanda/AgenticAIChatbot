using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AgenticAIChatbot
{
    public class SLAQueryService
    {
        private readonly Kernel _kernel;
        private readonly ILogger<RaasAgenticAIFunction> _logger;

        public SLAQueryService(Kernel kernel, ILogger<RaasAgenticAIFunction> logger)
        {
            _kernel = kernel;
            _logger = logger;
        }

        public async Task<string> GetShipmentSummaryAsync(string userMessage)
        {
            var extractFilterPrompt = @"You are a supply chain data assistant. Extract filter criteria from the following user message.
                                        Return a JSON object with the following potential fields, only including fields that are mentioned:
                                        - shipmentMode (string): e.g., 'Air', 'Ocean', 'Surface'
                                        - originCity (string): city name
                                        - destinationCity (string): city name
                                        - atRisk (boolean): true if 'at risk' or 'SLA breach' is mentioned
                                        - shipmentCreationDateTime (string): 'today', 'this week', 'this month', etc.
                                        - deliveryETADateTime (string): 'today', 'this week', 'this month', etc.

                                         User message: {{$userMessage}}

                                         Return ONLY a valid JSON object WITHOUT markdown formatting or code block syntax.
                                         ";

            var filterCriteriaResult = await _kernel.InvokePromptAsync(extractFilterPrompt, new()
            {
                ["userMessage"] = userMessage
            });

            var jsonContent = CleanJsonResponse(filterCriteriaResult.ToString());
            _logger.LogInformation($"Cleaned JSON: {jsonContent}");

            FilterCriteria filterCriteria;
            try
            {
                filterCriteria = JsonConvert.DeserializeObject<FilterCriteria>(jsonContent) ?? new();
            }
            catch (JsonException ex)
            {
                _logger.LogError("JSON parsing failed: {Message}", ex.Message);
                throw new InvalidOperationException("Failed to extract filter criteria from the message.");
            }

            var queryParams = new KernelArguments();

            if (!string.IsNullOrEmpty(filterCriteria.ShipmentMode))
                queryParams["shipmentMode"] = filterCriteria.ShipmentMode;

            if (!string.IsNullOrEmpty(filterCriteria.OriginCity))
                queryParams["originCity"] = filterCriteria.OriginCity;

            if (!string.IsNullOrEmpty(filterCriteria.DestinationCity))
                queryParams["destinationCity"] = filterCriteria.DestinationCity;

            if (filterCriteria.AtRisk == true)
                queryParams["atRisk"] = "true";

            if (!string.IsNullOrEmpty(filterCriteria.ShipmentCreationDateTime))
                queryParams["shipmentCreationDateTime"] = filterCriteria.ShipmentCreationDateTime;

            if (!string.IsNullOrEmpty(filterCriteria.DeliveryETADateTime))
                queryParams["deliveryETADateTime"] = filterCriteria.DeliveryETADateTime;

            // Invoke the shipments function
            var shipmentJson = await _kernel.InvokeAsync("Shipments", "GetFilteredShipments", queryParams);

            var shipmentsWithPredictions = await _kernel.InvokeAsync("Predictor", "PredictSla", new()
            {
                ["shipmentsJson"] = shipmentJson.ToString()
            });

            string summaryPrompt = @"You are a supply chain analyst. Based on this shipment data with SLA breach predictions, summarize which shipments are most likely to miss SLA and why.
                                     Look at the 'slaBreach' property which indicates our prediction of whether a shipment will miss its SLA.
                                     User asked about: {{$userMessage}}
                                     Shipments:
                                     {{$shipments}}
                                     Respond in a conversational, helpful tone addressing the user's query directly. Mention key shipments at risk and their risk factors.
                                     If there are no shipments found, politely inform the user and suggest they try a broader search.
                                     ";

            var summary = await _kernel.InvokePromptAsync(summaryPrompt, new()
            {
                ["userMessage"] = userMessage,
                ["shipments"] = shipmentsWithPredictions.ToString()
            });

            return summary.ToString();
        }

        private static string CleanJsonResponse(string llmResponse)
        {
            string cleaned = Regex.Replace(llmResponse, @"^[\s\S]*?```(?:json)?|```[\s\S]*?$", "", RegexOptions.Multiline).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "{}" : cleaned;
        }
    }
}
