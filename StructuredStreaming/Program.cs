using Azure.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;
using StructuredStreaming.Core;

namespace AzureOpenAIStreamingDemo
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Initialize the client for Azure OpenAI
            string apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY") ?? "your-azure-api-key";
            string endpoint = "https://yours.openai.azure.com/";
            string deploymentName = "gpt-4o"; // e.g., "gpt-4o" or your custom deployment

            // Configure client options for Azure OpenAI
            AzureOpenAIClient azureClient = new(new Uri(endpoint), new ApiKeyCredential(apiKey));
            ChatClient client = azureClient.GetChatClient(deploymentName);

            // Define the custom prompt to generate a large JSON object
            string customPrompt = @"Generate a JSON object with the following fields:
                - 'person': a object with two fields: 'name' (string) and 'city' (string),
                - 'story': a very long string (at least 200 words) describing a person's life story. Do not use markdown format, start with '{' directly.,
                - 'characters': an array of objects, each object has two fields: 'name' (string) and 'age' (number).";

            var messages = new ChatMessage[]
            {
                new UserChatMessage(customPrompt)
            };

            Console.WriteLine("Starting to receive streaming response from Azure OpenAI...\n");

            // Create JSON stream parser
            await using var jsonParser = new JsonStreamParser();

            // Start event processing task
            var eventProcessingTask = ProcessEventsAsync(jsonParser);

            // Stream the chat completion
            IAsyncEnumerable<StreamingChatCompletionUpdate> completionUpdates = 
                client.CompleteChatStreamingAsync(messages);

            try
            {
                await foreach (StreamingChatCompletionUpdate update in completionUpdates)
                {
                    if (update.ContentUpdate.Count > 0)
                    {
                        string delta = update.ContentUpdate[0].Text;
                        if (!string.IsNullOrEmpty(delta))
                        {
                            // Process the delta through our custom parser
                            await jsonParser.ProcessChunkAsync(delta);
                        }
                    }
                }

                // Complete the parsing process
                await jsonParser.CompleteAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            // Wait for event processing to complete
            await eventProcessingTask;

            Console.WriteLine("\nStreaming completed.");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private static async Task ProcessEventsAsync(JsonStreamParser parser)
        {
            string lastStringPropertyName = string.Empty;
            await foreach (var evt in parser.GetEventsAsync())
            {
                switch (evt)
                {
                    case JsonStringValueEvent stringEvent:
                        if (stringEvent.PropertyName != lastStringPropertyName)
                        {
                            Console.WriteLine($"\nString value for property {stringEvent.PropertyName}: ");
                            lastStringPropertyName = stringEvent.PropertyName;
                        }
                        Console.Write(stringEvent.Chunk);
                        if (stringEvent.IsFinal)
                        {
                            Console.WriteLine($"\nCompleted string value for property: {stringEvent.PropertyName}");
                        }
                        break;
                    
                    case JsonComplexValueEvent complexEvent:
                        Console.WriteLine($"\nComplex {(complexEvent.IsObject ? "object" : "array")} for property {complexEvent.PropertyName}: {complexEvent.Value}...");
                        break;
                    
                    case JsonPrimitiveValueEvent primitiveEvent:
                        Console.WriteLine($"\nPrimitive value for property {primitiveEvent.PropertyName}: {primitiveEvent.Value}");
                        break;
                    
                    case JsonCompleteEvent completeEvent:
                        Console.WriteLine($"\nJSON parsing completed. Valid JSON: {completeEvent.IsValidJson}");
                        break;
                    
                    case JsonErrorEvent errorEvent:
                        Console.WriteLine($"\nError: {errorEvent.Message}");
                        break;
                }
            }
        }
    }
}