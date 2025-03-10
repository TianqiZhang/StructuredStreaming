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
            string endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "https://yours.openai.azure.com/";
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

            Console.WriteLine("\nStarting to receive streaming response from Azure OpenAI...\n");

            // Create JSON stream parser - you can switch between implementations
            IJsonStreamParser jsonParser = new Utf8JsonStreamParser();
            // Alternatively use the character-by-character implementation:
            // IJsonStreamParser jsonParser = new CharacterByCharacterJsonStreamParser();
            
            // Queue to store events for processing
            var eventQueue = new Queue<JsonStreamEvent>();

            // Stream the chat completion
            IAsyncEnumerable<StreamingChatCompletionUpdate> completionUpdates = 
                client.CompleteChatStreamingAsync(messages);

            string lastStringPropertyName = string.Empty; // Track the last processed string property name
            try
            {
                await foreach (StreamingChatCompletionUpdate update in completionUpdates)
                {
                    if (update.ContentUpdate.Count > 0)
                    {
                        string delta = update.ContentUpdate[0].Text;
                        if (!string.IsNullOrEmpty(delta))
                        {
                            // Process the delta through our parser synchronously
                            var events = jsonParser.ProcessChunk(delta);
                            
                            // Queue up events for processing
                            foreach (var evt in events)
                            {
                                eventQueue.Enqueue(evt);
                            }
                            
                            // Process any available events
                            ProcessEvents(eventQueue, ref lastStringPropertyName);
                        }
                    }
                }

                // Complete the parsing process and process any final events
                var finalEvents = jsonParser.Complete();
                foreach (var evt in finalEvents)
                {
                    eventQueue.Enqueue(evt);
                }
                ProcessEvents(eventQueue, ref lastStringPropertyName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine("\nStreaming completed.");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private static void ProcessEvents(Queue<JsonStreamEvent> eventQueue, ref string lastStringPropertyName)
        {            
            while (eventQueue.Count > 0)
            {
                var evt = eventQueue.Dequeue();
                
                switch (evt)
                {
                    case JsonStringValueEvent stringEvent:
                        if (stringEvent.PropertyName != lastStringPropertyName)
                        {
                            Console.WriteLine($"\nString value for property \"{stringEvent.PropertyName}\": ");
                            lastStringPropertyName = stringEvent.PropertyName;
                        }
                        Console.Write(stringEvent.Chunk);
                        if (stringEvent.IsFinal)
                        {
                            Console.WriteLine($"\nCompleted string value for property: \"{stringEvent.PropertyName}\"");
                        }
                        break;
                    
                    case JsonComplexValueEvent complexEvent:
                        Console.WriteLine($"\nComplex {(complexEvent.IsObject ? "object" : "array")} for property \"{complexEvent.PropertyName}\": {complexEvent.Value}");
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