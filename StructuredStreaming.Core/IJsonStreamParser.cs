using System;
using System.Collections.Generic;

namespace StructuredStreaming.Core
{
    /// <summary>
    /// Interface for a streaming JSON parser that processes input chunks incrementally and emits structured events.
    /// </summary>
    public interface IJsonStreamParser : IDisposable
    {
        /// <summary>
        /// Processes a chunk of JSON data synchronously and returns any complete events that were produced.
        /// </summary>
        /// <param name="chunk">A string containing a portion of JSON data to process</param>
        /// <returns>A list of JSON stream events that were produced, or an empty list if no events were ready</returns>
        IReadOnlyList<JsonStreamEvent> ProcessChunk(string chunk);

        /// <summary>
        /// Completes the parsing process and returns any final events.
        /// </summary>
        /// <returns>A list of final events, including completion status</returns>
        IReadOnlyList<JsonStreamEvent> Complete();
    }
}
