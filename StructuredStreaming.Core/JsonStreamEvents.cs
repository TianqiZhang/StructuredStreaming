using System;

namespace StructuredStreaming.Core
{
    /// <summary>
    /// Base class for all JSON streaming events emitted by the JsonStreamParser
    /// </summary>
    public abstract class JsonStreamEvent
    {
        /// <summary>
        /// The property name this event is associated with, or null for root-level events
        /// </summary>
        public string? PropertyName { get; }

        protected JsonStreamEvent(string? propertyName = null)
        {
            PropertyName = propertyName;
        }
    }

    /// <summary>
    /// Event emitted for a chunk of a string value
    /// </summary>
    public class JsonStringValueEvent : JsonStreamEvent
    {
        /// <summary>
        /// The string value fragment
        /// </summary>
        public string Chunk { get; }
        
        /// <summary>
        /// Indicates if this is the final chunk of the string value
        /// </summary>
        public bool IsFinal { get; }

        public JsonStringValueEvent(string propertyName, string chunk, bool isFinal) 
            : base(propertyName)
        {
            Chunk = chunk;
            IsFinal = isFinal;
        }
    }

    /// <summary>
    /// Event emitted when a complex value (object or array) is completely parsed
    /// </summary>
    public class JsonComplexValueEvent : JsonStreamEvent
    {
        /// <summary>
        /// The complete JSON string representing the complex value
        /// </summary>
        public string Value { get; }

        /// <summary>
        /// Indicates if this is an object ('{') or array ('[')
        /// </summary>
        public bool IsObject { get; }

        public JsonComplexValueEvent(string propertyName, string value, bool isObject) 
            : base(propertyName)
        {
            Value = value;
            IsObject = isObject;
        }
    }

    /// <summary>
    /// Event emitted when a primitive value is parsed (number, boolean, null)
    /// </summary>
    public class JsonPrimitiveValueEvent : JsonStreamEvent
    {
        /// <summary>
        /// The string representation of the primitive value
        /// </summary>
        public string Value { get; }

        public JsonPrimitiveValueEvent(string propertyName, string value) 
            : base(propertyName)
        {
            Value = value;
        }
    }

    /// <summary>
    /// Event emitted when JSON parsing is completed
    /// </summary>
    public class JsonCompleteEvent : JsonStreamEvent
    {
        /// <summary>
        /// Indicates if the JSON structure was properly terminated
        /// </summary>
        public bool IsValidJson { get; }

        public JsonCompleteEvent(bool isValidJson = true) : base(null)
        {
            IsValidJson = isValidJson;
        }
    }

    /// <summary>
    /// Event emitted when an error occurs during parsing
    /// </summary>
    public class JsonErrorEvent : JsonStreamEvent
    {
        /// <summary>
        /// The error message
        /// </summary>
        public string Message { get; }

        public JsonErrorEvent(string message) : base(null)
        {
            Message = message;
        }
    }
}