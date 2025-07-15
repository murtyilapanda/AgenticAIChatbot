using AgenticAIChatbot;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("local.settings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var endpoint = config["COSMOS_ENDPOINT"];
        var key = config["COSMOS_KEY"];
        var db = config["COSMOS_DATABASE_ID"];
        var container = config["COSMOS_CONTAINER_ID"];
        // ➤ Semantic Kernel setup
        var openAiKey = config["AZURE_OPENAI_KEY"];
        var openAiEndpoint = config["AZURE_OPENAI_ENDPOINT"];
        var deploymentName = config["AZURE_OPENAI_DEPLOYMENT"];

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddAzureOpenAIChatCompletion(deploymentName, openAiEndpoint, openAiKey);
        var kernel = kernelBuilder.Build();
        CosmosClientOptions options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway
        };

        var cosmosClient = new CosmosClient(endpoint, key, options);
        services.AddSingleton(cosmosClient);
        services.AddSingleton(sp => cosmosClient.GetContainer(db, container));
        services.AddSingleton(kernel);
        services.AddSingleton<RiskAssessment>();
        services.AddHttpClient<ShipmentQueryService>();
        services.AddSingleton<SLAQueryService>();
        services.AddHttpClient();
        services.AddSingleton<SlaPredictionPlugin>();
        services.AddHttpClient<CosmosDbPlugin>();
        services.AddSingleton<CosmosDbPlugin>();


    })
    .Build();

host.Run();
