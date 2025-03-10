# Structured Streaming with Azure OpenAI

This repository demonstrates how to stream and parse partial JSON responses from Azure OpenAI using a custom JSON stream parser. The application sends a custom prompt to Azure OpenAI, receives the streaming response, and processes it in real-time.

## Project Structure

- `StructuredStreaming.sln`: Solution file for the project.
- `StructuredStreaming.Core/`: Core library for JSON stream parsing.
- `StructuredStreaming.Sample/`: Sample application demonstrating how to use the JSON stream parser with Azure OpenAI.
- `StructuredStreaming.Tests/`: Unit tests for the core library.

## Prerequisites

- .NET 8.0 SDK or later
- Azure OpenAI API key
- Azure OpenAI endpoint

## Getting Started

1. Clone the repository:

```sh
 git clone <repository-url>
```

2. Navigate to the project directory:

```sh
 cd StructuredStreaming
```

3. Set the Azure OpenAI API key and endpoint as environment variables:

```sh
 setx AZURE_OPENAI_API_KEY "your-azure-api-key"
 setx AZURE_OPENAI_ENDPOINT "https://yours.openai.azure.com/"
```

4. Build the project:

```sh
 dotnet build
```

5. Run the sample application:

```sh
 dotnet run --project StructuredStreaming.Sample
```

## Usage

The sample application sends a custom prompt to Azure OpenAI to generate a large JSON object and processes the streaming response using a custom JSON stream parser. The parsed events are printed to the console in real-time.

## How the JSON Stream Parser Works

The `JsonStreamParser` class processes JSON data arriving in fragments and emits structured events representing the parsed JSON structure. It handles different states of JSON parsing, such as waiting for an object start, reading property names, processing string values, and handling nested structures. The parser uses a state machine to manage these states and a channel to communicate events to consumers.

### Parser Behavior and Limitations

The parser has specific behaviors regarding how it handles different types of JSON properties:

1. **First-level properties only**: The incremental parsing and streaming is optimized for the first level of JSON properties in the root object.

2. **String properties**: For string properties at the root level, the parser will emit events in real-time as chunks of the string are received. This allows for immediate processing of partial string content.

3. **Complex properties**: For objects and arrays, the parser will accumulate the entire nested structure and emit a single event once the complete object or array is received.

4. **Primitive properties**: For numbers, booleans, and null values, the parser will emit a single event when the complete value is received.

This design makes the parser particularly well-suited for handling large JSON responses where some string properties contain substantial content that you want to process incrementally, while maintaining the structure of the overall JSON object.

### Example Usage

To use the `JsonStreamParser`, create an instance of the parser, process JSON chunks incrementally, and read the parsed events:

```csharp
var parser = new JsonStreamParser();

// Process JSON chunks
await parser.ProcessChunkAsync(jsonChunk);

// Complete the parsing process
await parser.CompleteAsync();

// Read parsed events
await foreach (var evt in parser.GetEventsAsync())
{
    switch (evt)
    {
        case JsonStringValueEvent stringEvent:
            // Handle string chunks as they come in
            Console.Write(stringEvent.Chunk);
            break;
        
        case JsonComplexValueEvent complexEvent:
            // Handle complete objects or arrays
            ProcessComplexValue(complexEvent.Value);
            break;
        
        case JsonPrimitiveValueEvent primitiveEvent:
            // Handle primitive values (numbers, booleans, null)
            ProcessPrimitiveValue(primitiveEvent.PropertyName, primitiveEvent.Value);
            break;
    }
}
```

## License

This project is licensed under the MIT License.
