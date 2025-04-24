using Core.Utilities.Config;
// Add import for Plugins
using Core.Utilities.Plugins;
// Add import required for StockService
using Core.Utilities.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
// Add ChatCompletion import
using Microsoft.SemanticKernel.ChatCompletion;
// Temporarily added to enable Semantic Kernel tracing
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.SemanticKernel.Agents.AzureAI;


// Initialize the kernel with chat completion
IKernelBuilder builder = KernelBuilderProvider.CreateKernelWithChatCompletion();
// Enable tracing
builder.Services.AddLogging(services => services.AddConsole().SetMinimumLevel(LogLevel.Trace));
Kernel kernel = builder.Build();

// Initialize Time plugin and registration in the kernel
kernel.Plugins.AddFromObject(new TimeInformationPlugin());

var connectionString = AISettingsProvider.GetSettings().AIFoundryProject.ConnectionString;
System.Console.WriteLine(  $"Connection string: {connectionString}");

var bingConnectionId = AISettingsProvider.GetSettings().AIFoundryProject.GroundingWithBingConnectionId;
System.Console.WriteLine(  $"Bing connection id: {bingConnectionId}");

var projectClient = new AIProjectClient(connectionString, new DefaultAzureCredential());

ConnectionResponse bingConnection = await projectClient.GetConnectionsClient().GetConnectionAsync(bingConnectionId);
var connectionId = bingConnection.Id;

ToolConnectionList connectionList = new ToolConnectionList
{
    ConnectionList = { new ToolConnection(connectionId) }
};
BingGroundingToolDefinition bingGroundingTool = new BingGroundingToolDefinition(connectionList);

var clientProvider =  AzureAIClientProvider.FromConnectionString(connectionString, new AzureCliCredential());
AgentsClient client = clientProvider.Client.GetAgentsClient();
var definition = await client.CreateAgentAsync(
    "gpt-4o",
    instructions:
            """
            Your responsibility is to find the stock sentiment for a given Stock.

            RULES:
            - Report a stock sentiment scale from 1 to 10 where stock sentiment is 1 for sell and 10 for buy.
            - Only use current data reputable sources such as Yahoo Finance, MarketWatch, Fidelity and similar.
            - Provide the stock sentiment scale in your response and a recommendation to buy, hold or sell.
            """,
    tools:
    [
        bingGroundingTool
    ]);
var agent = new AzureAIAgent(definition, clientProvider)
{
    Kernel = kernel,
};

// Create a thread for the agent conversation.
AgentThread thread = await client.CreateThreadAsync();

// Initialize Stock Data Plugin and register it in the kernel
HttpClient httpClient = new();
StockDataPlugin stockDataPlugin = new(new StocksService(httpClient));
kernel.Plugins.AddFromObject(stockDataPlugin);

// Get chatCompletionService and initialize chatHistory with system prompt
var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
ChatHistory chatHistory = new("You are a friendly financial advisor that only emits financial advice in a creative and funny tone");
// Remove the promptExecutionSettings and kernelArgs initialization code
// Add system prompt
OpenAIPromptExecutionSettings promptExecutionSettings = new()
{
    // Add Auto invoke kernel functions as the tool call behavior
    ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
};

// Initialize kernel arguments
KernelArguments kernelArgs = new(promptExecutionSettings);

// Execute program.

const string terminationPhrase = "quit";
string? userInput;
do
{
    Console.Write("User > ");
    userInput = Console.ReadLine();

    if (userInput is not null and not terminationPhrase)
    {
        Console.Write("Assistant > ");
        // Initialize fullMessage variable and add user input to chat history
        string fullMessage = "";
        chatHistory.AddUserMessage(userInput);

        ChatMessageContent message = new(AuthorRole.User, userInput);
        await agent.AddChatMessageAsync(thread.Id, message);

        await foreach (ChatMessageContent response in agent.InvokeAsync(thread.Id))
        {
            string contentExpression = string.IsNullOrWhiteSpace(response.Content) ? string.Empty : response.Content;
            chatHistory.AddAssistantMessage(contentExpression);
            Console.WriteLine($"{contentExpression}");
        }

        Console.WriteLine();
    }
}
while (userInput != terminationPhrase);

