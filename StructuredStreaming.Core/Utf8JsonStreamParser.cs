using System;
using System.Collections.Generic;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.IO;

namespace StructuredStreaming.Core
{
    /// <summary>
    /// A streaming JSON parser implementation that uses System.Text.Json.Utf8JsonReader internally
    /// to process input chunks incrementally and emit structured events.
    /// </summary>
    public class Utf8JsonStreamParser : IJsonStreamParser
    {
        private readonly List<JsonStreamEvent> _pendingEvents = new List<JsonStreamEvent>();
        private byte[] _buffer = new byte[4096];
        private int _dataLength = 0;
        private bool _isCompleted = false;
        private string? _currentProperty = null;
        private JsonReaderState _readerState;
        
        // For tracking nested structures
        private int _depth = 0;
        private bool _insideString = false;
        private readonly StringBuilder _valueBuilder = new StringBuilder();
        private readonly Stack<(int Depth, char Type)> _structureStack = new Stack<(int Depth, char Type)>();
        
        // For string value handling
        private bool _insideStringValue = false;
        private readonly StringBuilder _stringBuffer = new StringBuilder();

        /// <summary>
        /// Initializes a new instance of the Utf8JsonStreamParser.
        /// </summary>
        public Utf8JsonStreamParser()
        {
            _readerState = new JsonReaderState();
        }

        /// <inheritdoc />
        public IReadOnlyList<JsonStreamEvent> ProcessChunk(string chunk)
        {
            _pendingEvents.Clear();
            
            if (_isCompleted || string.IsNullOrEmpty(chunk))
            {
                return Array.Empty<JsonStreamEvent>();
            }

            try
            {
                // Convert string to UTF-8 bytes and add to buffer
                byte[] inputBytes = Encoding.UTF8.GetBytes(chunk);
                EnsureBufferCapacity(_dataLength + inputBytes.Length);
                Buffer.BlockCopy(inputBytes, 0, _buffer, _dataLength, inputBytes.Length);
                _dataLength += inputBytes.Length;

                // Process as much data as possible
                ProcessBuffer();

                return _pendingEvents.ToArray();
            }
            catch (Exception ex)
            {
                _pendingEvents.Add(new JsonErrorEvent($"Error processing JSON chunk: {ex.Message}"));
                return _pendingEvents.ToArray();
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<JsonStreamEvent> Complete()
        {
            _pendingEvents.Clear();
            
            try
            {
                // Try to process any remaining data
                ProcessBuffer(true);
                
                // If we were inside a string value, finalize it
                if (_insideStringValue)
                {
                    EmitStringValue("", true);
                    _insideStringValue = false;
                }
                
                // If we have any incomplete nested structures, report them as errors
                if (_structureStack.Count > 0)
                {
                    _pendingEvents.Add(new JsonErrorEvent("Incomplete JSON: missing closing brackets or braces"));
                }
                
                // Signal that parsing is complete
                _isCompleted = true;
                _pendingEvents.Add(new JsonCompleteEvent(_structureStack.Count == 0));
                
                return _pendingEvents.ToArray();
            }
            catch (Exception ex)
            {
                _pendingEvents.Add(new JsonErrorEvent($"Error completing JSON parsing: {ex.Message}"));
                _pendingEvents.Add(new JsonCompleteEvent(false));
                return _pendingEvents.ToArray();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Clean up resources if needed
        }

        private void ProcessBuffer(bool isFinal = false)
        {
            // No data to process
            if (_dataLength == 0) return;
            
            // Create reader over current buffer
            var reader = new Utf8JsonReader(
                new ReadOnlySpan<byte>(_buffer, 0, _dataLength),
                isFinal,
                _readerState);

            int bytesConsumed = 0;
            bool shouldContinue = true;
            
            // Process tokens until we can't read more
            while (shouldContinue)
            {
                try
                {
                    shouldContinue = reader.Read();
                    if (!shouldContinue) break;
                    
                    // Track depth changes for nested structures
                    HandleDepthChanges(reader);
                    
                    // Process the current token
                    ProcessToken(reader);
                    
                    // Track how much data has been consumed
                    bytesConsumed = (int)reader.BytesConsumed;
                }
                catch (JsonException ex)
                {
                    // Handle invalid JSON
                    _pendingEvents.Add(new JsonErrorEvent($"JSON parsing error: {ex.Message}"));
                    
                    // Try to skip to the next valid token if possible
                    if (!isFinal)
                    {
                        // For non-final chunks, we'll keep the rest of the buffer
                        bytesConsumed = Math.Max(bytesConsumed, 1); // Ensure we consume at least one byte
                    }
                    else
                    {
                        // For final chunks, consume everything
                        bytesConsumed = _dataLength;
                    }
                    
                    shouldContinue = false;
                }
            }
            
            // Update reader state for next chunk
            _readerState = reader.CurrentState;
            
            // Remove consumed bytes from buffer
            if (bytesConsumed > 0)
            {
                RemoveConsumedBytes(bytesConsumed);
            }
        }

        private void ProcessToken(Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    _currentProperty = reader.GetString();
                    _insideStringValue = false;
                    break;
                
                case JsonTokenType.StartObject:
                    if (_depth == 1) // Root object
                    {
                        // Do nothing for now
                    }
                    else if (_currentProperty != null && _depth == 2)
                    {
                        // Start a nested object, capture it entirely
                        _structureStack.Push((_depth, '{'));
                        _valueBuilder.Clear();
                        _valueBuilder.Append('{');
                    }
                    break;
                
                case JsonTokenType.EndObject:
                    if (_depth == 0) // Root object closed
                    {
                        // Do nothing special
                    }
                    else if (_structureStack.Count > 0 && _structureStack.Peek().Type == '{')
                    {
                        var (stackDepth, _) = _structureStack.Peek();
                        
                        // If we're closing the immediate object we opened
                        if (stackDepth == _depth + 1)
                        {
                            _structureStack.Pop();
                            _valueBuilder.Append('}');
                            
                            // If we closed the outermost structure, emit the complex value
                            if (_structureStack.Count == 0)
                            {
                                _pendingEvents.Add(new JsonComplexValueEvent(
                                    _currentProperty, 
                                    _valueBuilder.ToString(), 
                                    true));
                                
                                _valueBuilder.Clear();
                            }
                        }
                    }
                    break;
                
                case JsonTokenType.StartArray:
                    if (_currentProperty != null)
                    {
                        // Start tracking this array
                        _structureStack.Push((_depth, '['));
                        _valueBuilder.Clear();
                        _valueBuilder.Append('[');
                    }
                    break;
                
                case JsonTokenType.EndArray:
                    if (_structureStack.Count > 0 && _structureStack.Peek().Type == '[')
                    {
                        var (stackDepth, _) = _structureStack.Peek();
                        
                        // If we're closing the immediate array we opened
                        if (stackDepth == _depth + 1)
                        {
                            _structureStack.Pop();
                            _valueBuilder.Append(']');
                            
                            // If we closed the outermost structure, emit the complex value
                            if (_structureStack.Count == 0)
                            {
                                _pendingEvents.Add(new JsonComplexValueEvent(
                                    _currentProperty, 
                                    _valueBuilder.ToString(), 
                                    false));
                                
                                _valueBuilder.Clear();
                            }
                        }
                    }
                    break;
                
                case JsonTokenType.String:
                    if (_currentProperty != null)
                    {
                        if (_structureStack.Count > 0)
                        {
                            // String inside a nested structure, add it to the value builder
                            string stringValue = reader.GetString() ?? "";
                            _valueBuilder.Append(JsonEncode(stringValue));
                        }
                        else
                        {
                            // Top-level string value, emit it
                            if (!_insideStringValue)
                            {
                                _insideStringValue = true;
                                _stringBuffer.Clear();
                            }
                            
                            string value = reader.GetString() ?? "";
                            EmitStringValue(value, true);
                            _insideStringValue = false;
                        }
                    }
                    break;
                
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    if (_currentProperty != null)
                    {
                        if (_structureStack.Count > 0)
                        {
                            // Value inside a nested structure
                            string rawValue = reader.TokenType switch
                            {
                                JsonTokenType.Number => reader.TryGetInt64(out var longValue) 
                                    ? longValue.ToString() 
                                    : reader.GetDouble().ToString(),
                                JsonTokenType.True => "true",
                                JsonTokenType.False => "false",
                                JsonTokenType.Null => "null",
                                _ => ""
                            };
                            
                            _valueBuilder.Append(rawValue);
                        }
                        else
                        {
                            // Primitive value at the top level
                            string value = reader.TokenType switch
                            {
                                JsonTokenType.Number => reader.TryGetInt64(out var longValue) 
                                    ? longValue.ToString() 
                                    : reader.GetDouble().ToString(),
                                JsonTokenType.True => "true",
                                JsonTokenType.False => "false",
                                JsonTokenType.Null => "null",
                                _ => ""
                            };
                            
                            _pendingEvents.Add(new JsonPrimitiveValueEvent(_currentProperty, value));
                        }
                    }
                    break;

                // Handle other token types if needed
            }
            
            // If we're inside a nested structure, append tokens to the value
            if (_structureStack.Count > 0 && reader.TokenType != JsonTokenType.StartObject 
                && reader.TokenType != JsonTokenType.StartArray 
                && reader.TokenType != JsonTokenType.EndObject
                && reader.TokenType != JsonTokenType.EndArray
                && reader.TokenType != JsonTokenType.String
                && reader.TokenType != JsonTokenType.PropertyName
                && reader.TokenType != JsonTokenType.Number
                && reader.TokenType != JsonTokenType.True
                && reader.TokenType != JsonTokenType.False
                && reader.TokenType != JsonTokenType.Null)
            {
                // Append any other character (like comma, etc.) for complex values
                if (reader.HasValueSequence)
                {
                    foreach (var segment in reader.ValueSequence)
                    {
                        _valueBuilder.Append(Encoding.UTF8.GetString(segment.Span));
                    }
                }
                else if (reader.ValueSpan.Length > 0)
                {
                    _valueBuilder.Append(Encoding.UTF8.GetString(reader.ValueSpan));
                }
            }
        }

        private void HandleDepthChanges(Utf8JsonReader reader)
        {
            // Track depth changes
            if (reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray)
            {
                _depth++;
            }
            else if (reader.TokenType == JsonTokenType.EndObject || reader.TokenType == JsonTokenType.EndArray)
            {
                _depth--;
            }
            
            // Track if we're inside a string in a complex value
            if (_structureStack.Count > 0)
            {
                if (reader.TokenType == JsonTokenType.String && !_insideString)
                {
                    _insideString = true;
                }
                else if (reader.TokenType == JsonTokenType.String && _insideString)
                {
                    _insideString = false;
                }
            }
        }

        private void EmitStringValue(string chunk, bool isFinal)
        {
            if (!string.IsNullOrEmpty(chunk))
            {
                _stringBuffer.Append(chunk);
            }
            
            if (isFinal && _currentProperty != null)
            {
                _pendingEvents.Add(new JsonStringValueEvent(
                    _currentProperty, 
                    _stringBuffer.ToString(), 
                    true));
                _stringBuffer.Clear();
            }
            else if (!string.IsNullOrEmpty(chunk) && _currentProperty != null)
            {
                _pendingEvents.Add(new JsonStringValueEvent(
                    _currentProperty,
                    chunk,
                    false));
            }
        }

        private void EnsureBufferCapacity(int requiredCapacity)
        {
            if (_buffer.Length < requiredCapacity)
            {
                int newCapacity = Math.Max(_buffer.Length * 2, requiredCapacity);
                byte[] newBuffer = new byte[newCapacity];
                Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _dataLength);
                _buffer = newBuffer;
            }
        }

        private void RemoveConsumedBytes(int bytesConsumed)
        {
            if (bytesConsumed <= 0) return;
            
            if (bytesConsumed >= _dataLength)
            {
                // All data consumed
                _dataLength = 0;
            }
            else
            {
                // Move unconsumed data to front of buffer
                Buffer.BlockCopy(_buffer, bytesConsumed, _buffer, 0, _dataLength - bytesConsumed);
                _dataLength -= bytesConsumed;
            }
        }
        
        // Helper to properly encode strings in complex values
        private string JsonEncode(string value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStringValue(value);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
