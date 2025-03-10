using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StructuredStreaming.Core
{
    /// <summary>
    /// A streaming JSON parser that processes input chunks incrementally and emits structured events.
    /// This parser is designed to handle JSON data arriving in fragments (as it would from a streaming API)
    /// and produce a stream of events representing the parsed JSON structure.
    /// </summary>
    public class JsonStreamParser : IDisposable
    {
        /// <summary>
        /// Represents the current state of the parser in the JSON parsing state machine.
        /// </summary>
        private enum ParserState
        {
            WaitingForObjectStart,     // Waiting for the initial '{' character
            WaitingForProperty,        // Expecting a property name or end of object
            InsidePropertyName,        // Currently reading a property name
            WaitingForColon,           // Expecting the ':' separator after property name
            WaitingForValue,           // Waiting for the beginning of a value
            InsideStringValue,         // Currently inside a string value
            InsidePrimitiveValue,      // Inside a primitive value (number, boolean, null)
            InsideNestedStructure      // Inside a nested object or array
        }

        // Current state of the parser
        private ParserState _state = ParserState.WaitingForObjectStart;
        
        // Buffer for holding incoming data that hasn't been fully processed yet
        private readonly StringBuilder _buffer = new StringBuilder();
        
        // Multi-purpose buffer for accumulating property names, primitives, etc.
        private readonly StringBuilder _valueBuffer = new StringBuilder();
        
        // Name of the current property being processed
        private string? _currentProperty = null;
        
        // Flag to track if the previous character was an escape character
        private bool _isEscaped = false;
        
        // Flag to track if we're inside a string within a nested structure
        private bool _insideString = false;
        
        // Stack to track opening brackets/braces for proper nesting
        private readonly Stack<char> _nestedStructureStack = new Stack<char>();
        
        // Collection for storing events created during synchronous processing
        private readonly List<JsonStreamEvent> _pendingEvents = new List<JsonStreamEvent>();

        /// <summary>
        /// Initializes a new instance of the JsonStreamParser.
        /// </summary>
        public JsonStreamParser()
        {
        }

        /// <summary>
        /// Processes a chunk of JSON data synchronously and returns any complete events that were produced.
        /// </summary>
        /// <param name="chunk">A string containing a portion of JSON data to process</param>
        /// <returns>A list of JSON stream events that were produced, or an empty list if no events were ready</returns>
        public IReadOnlyList<JsonStreamEvent> ProcessChunk(string chunk)
        {
            _pendingEvents.Clear();
            
            try
            {
                // Append new data to the buffer
                _buffer.Append(chunk);
                
                // Process buffered data
                while (_buffer.Length > 0)
                {
                    if (_state == ParserState.InsideStringValue)
                    {
                        int termIdx = FindTerminatingQuoteInBuffer();
                        if (termIdx == -1)
                        {
                            // No terminating quote found; emit entire buffer as one chunk.
                            string safeChunk = _buffer.ToString();
                            _buffer.Clear();
                            EmitStringValue(safeChunk, false);
                            break; // Wait for more data.
                        }
                        else
                        {
                            // If there is any safe text before the terminating quote, emit it.
                            if (termIdx > 0)
                            {
                                string safeChunk = _buffer.ToString(0, termIdx);
                                EmitStringValue(safeChunk, false);
                            }
                            // Remove the emitted text and the terminating quote.
                            _buffer.Remove(0, termIdx + 1);
                            EmitStringValue("", true);
                            _state = ParserState.WaitingForProperty;
                            continue;
                        }
                    }
                    else if (_state == ParserState.InsideNestedStructure)
                    {
                        // Try to process larger chunks of nested structures at once
                        ProcessNestedStructureChunk();
                    }
                    else
                    {
                        // Process one character at a time for other states
                        if (_buffer.Length > 0)
                        {
                            char c = _buffer[0];
                            _buffer.Remove(0, 1);
                            ProcessCharacter(c);
                        }
                    }
                }
                
                // Return a copy of the collected events
                return _pendingEvents.ToArray();
            }
            catch (Exception ex)
            {
                EmitError($"Error processing JSON chunk: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Completes the parsing process and returns any final events.
        /// </summary>
        /// <returns>A list of final events, including completion status</returns>
        public IReadOnlyList<JsonStreamEvent> Complete()
        {
            _pendingEvents.Clear();
            
            try
            {
                // If we're in the middle of parsing, try to emit what we have so far
                if (_state == ParserState.InsideStringValue && _currentProperty != null)
                {
                    EmitStringValue("", true);
                }
                else if (_state == ParserState.InsidePrimitiveValue && _currentProperty != null)
                {
                    _pendingEvents.Add(new JsonPrimitiveValueEvent(_currentProperty, _valueBuffer.ToString()));
                }
                
                // Check if JSON is properly terminated
                bool isCompleteJson = _state == ParserState.WaitingForObjectStart;
                
                // Report malformed JSON if we haven't seen a proper end of the JSON object
                if (!isCompleteJson)
                {
                    EmitError("Malformed JSON: missing closing brackets or incomplete data");
                }
                
                // Signal that parsing is complete
                _pendingEvents.Add(new JsonCompleteEvent(isCompleteJson));
                
                return _pendingEvents.ToArray();
            }
            catch (Exception ex)
            {
                EmitError($"Error completing JSON parsing: {ex.Message}");
                return _pendingEvents.ToArray();
            }
        }

        /// <summary>
        /// Disposes the parser resources.
        /// </summary>
        public void Dispose()
        {
            // Clean up any resources if needed
        }

        // New helper to find the index of an unescaped terminating quote in _buffer.
        private int FindTerminatingQuoteInBuffer()
        {
            bool escape = false;
            for (int i = 0; i < _buffer.Length; i++)
            {
                char c = _buffer[i];
                if (c == '"' && !escape)
                {
                    return i;
                }
                escape = (c == '\\' && !escape);
            }
            return -1;
        }

        // New helper method to process nested structure chunks more efficiently
        private void ProcessNestedStructureChunk()
        {
            bool foundEnd = false;
            int i = 0;
            
            while (i < _buffer.Length && !foundEnd)
            {
                char c = _buffer[i++];
                _valueBuffer.Append(c);
                
                if (c == '\\' && _insideString && !_isEscaped)
                {
                    _isEscaped = true;
                }
                else
                {
                    if (c == '"' && !_isEscaped)
                    {
                        _insideString = !_insideString;
                    }
                    else if (!_insideString)
                    {
                        if (c == '{' || c == '[')
                        {
                            _nestedStructureStack.Push(c);
                        }
                        else if ((c == '}' && _nestedStructureStack.Count > 0 && _nestedStructureStack.Peek() == '{') ||
                                 (c == ']' && _nestedStructureStack.Count > 0 && _nestedStructureStack.Peek() == '['))
                        {
                            _nestedStructureStack.Pop();
                            
                            if (_nestedStructureStack.Count == 0)
                            {
                                foundEnd = true;
                            }
                        }
                    }
                    
                    if (_isEscaped)
                    {
                        _isEscaped = false;
                    }
                }
            }
            
            // Remove the processed portion from the buffer
            if (i > 0)
            {
                _buffer.Remove(0, i);
            }
            
            // If we found the end of the nested structure, emit it
            if (foundEnd)
            {
                bool isObject = _valueBuffer[0] == '{';
                _pendingEvents.Add(new JsonComplexValueEvent(_currentProperty, _valueBuffer.ToString(), isObject));
                
                _valueBuffer.Clear();
                _state = ParserState.WaitingForProperty;
            }
        }

        // Event emitting methods
        private void EmitStringValue(string chunk, bool isFinal)
        {
            _pendingEvents.Add(new JsonStringValueEvent(_currentProperty, chunk, isFinal));
        }

        private void EmitError(string message)
        {
            _pendingEvents.Add(new JsonErrorEvent(message));
        }

        // Core parsing logic: processes a single character based on the current parser state
        private void ProcessCharacter(char c)
        {
            switch (_state)
            {
                case ParserState.WaitingForObjectStart:
                    if (c == '{')
                    {
                        _state = ParserState.WaitingForProperty;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        EmitError($"Unexpected character: {c}, expected '{{' to start JSON object");
                    }
                    break;

                case ParserState.WaitingForProperty:
                    if (c == '"')
                    {
                        _valueBuffer.Clear();
                        _state = ParserState.InsidePropertyName;
                    }
                    else if (c == '}')
                    {
                        // Root object has ended, transition back to initial state
                        _state = ParserState.WaitingForObjectStart;
                    }
                    else if (c == ',' || char.IsWhiteSpace(c))
                    {
                        // Ignore commas and whitespace between properties
                    }
                    else
                    {
                        EmitError($"Unexpected character: {c}, expected a property name");
                    }
                    break;

                case ParserState.InsidePropertyName:
                    if (c == '"' && !_isEscaped)
                    {
                        _currentProperty = _valueBuffer.ToString();
                        _valueBuffer.Clear();
                        _state = ParserState.WaitingForColon;
                    }
                    else if (c == '\\' && !_isEscaped)
                    {
                        _isEscaped = true;
                    }
                    else
                    {
                        if (_isEscaped)
                            _isEscaped = false;
                        
                        _valueBuffer.Append(c);
                    }
                    break;

                case ParserState.WaitingForColon:
                    if (c == ':')
                    {
                        _state = ParserState.WaitingForValue;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        EmitError($"Unexpected character: {c}, expected ':'");
                    }
                    break;

                case ParserState.WaitingForValue:
                    if (c == '"')
                    {
                        _isEscaped = false;
                        _state = ParserState.InsideStringValue;
                    }
                    else if (c == '{' || c == '[')
                    {
                        _valueBuffer.Clear();
                        _valueBuffer.Append(c);
                        _nestedStructureStack.Clear();
                        _nestedStructureStack.Push(c);
                        _state = ParserState.InsideNestedStructure;
                        _insideString = false;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        _valueBuffer.Clear();
                        _valueBuffer.Append(c);
                        _state = ParserState.InsidePrimitiveValue;
                    }
                    break;

                case ParserState.InsidePrimitiveValue:
                    if (c == ',' || c == '}')
                    {
                        _pendingEvents.Add(new JsonPrimitiveValueEvent(_currentProperty, _valueBuffer.ToString()));
                        
                        _valueBuffer.Clear();
                        _state = ParserState.WaitingForProperty;
                        
                        // If we hit the end of object, put the '}' back to be processed in WaitingForProperty state
                        if (c == '}')
                        {
                            _buffer.Insert(0, c);
                        }
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        _valueBuffer.Append(c);
                    }
                    break;
            }
        }
    }
}
