using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace StructuredStreaming.Core
{
    /// <summary>
    /// A streaming JSON parser that processes input chunks incrementally and emits structured events.
    /// This parser is designed to handle JSON data arriving in fragments (as it would from a streaming API)
    /// and produce a stream of events representing the parsed JSON structure.
    /// </summary>
    public class JsonStreamParser : IAsyncDisposable
    {
        /// <summary>
        /// Represents the current state of the parser in the JSON parsing state machine.
        /// </summary>
        private enum ParserState
        {
            WaitingForObjectStart,     // Waiting for the initial '{' character
            WaitingForPropertyName,    // Expecting a property name or end of object
            InsidePropertyName,        // Currently reading a property name
            WaitingForColon,           // Expecting the ':' separator after property name
            WaitingForValueStart,      // Waiting for the beginning of a value
            InsideStringValue,         // Currently inside a string value
            InsidePrimitiveValue,      // Inside a primitive value (number, boolean, null)
            InsideNestedStructure,     // Inside a nested object or array
            WaitingForCommaOrEnd       // Expecting ',' for next property or '}' to end object
        }

        // Current state of the parser
        private ParserState _state = ParserState.WaitingForObjectStart;
        
        // Buffer for holding incoming data that hasn't been fully processed yet
        private readonly StringBuilder _buffer = new StringBuilder();
        
        // Buffer for accumulating the current property name being parsed
        private readonly StringBuilder _currentPropertyName = new StringBuilder();
        
        // Buffer for accumulating primitive values
        private readonly StringBuilder _primitiveValueBuffer = new StringBuilder();
        
        // Buffer for accumulating nested JSON structures (objects/arrays)
        private readonly StringBuilder _nestedStructureBuffer = new StringBuilder();
        
        // Name of the current property being processed
        private string? _currentProperty = null;
        
        // Flag to track if the previous character was an escape character
        private bool _isEscaped = false;
        
        // Flag to track if we're inside a string within a nested structure
        private bool _insideString = false;
        
        // Stack to track opening brackets/braces for proper nesting
        private readonly Stack<char> _nestedStructureStack = new Stack<char>();
        
        // Channel for communicating events to consumers
        private readonly Channel<JsonStreamEvent> _eventChannel;
        
        // Cancellation token source for cleanup
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>
        /// Initializes a new instance of the JsonStreamParser.
        /// </summary>
        /// <param name="capacity">The channel capacity for buffering events</param>
        public JsonStreamParser(int capacity = 100)
        {
            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            };
            
            _eventChannel = Channel.CreateBounded<JsonStreamEvent>(options);
        }

        /// <summary>
        /// Gets an async enumerable of JSON streaming events.
        /// </summary>
        public IAsyncEnumerable<JsonStreamEvent> GetEventsAsync(CancellationToken cancellationToken = default)
        {
            return _eventChannel.Reader.ReadAllAsync(
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _cts.Token).Token);
        }

        /// <summary>
        /// Processes a chunk of JSON data incrementally.
        /// </summary>
        /// <param name="chunk">A string containing a portion of JSON data to process</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        public async Task ProcessChunkAsync(string chunk, CancellationToken cancellationToken = default)
        {
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
                            await EmitStringChunkAsync(safeChunk, false, cancellationToken);
                            break; // Wait for more data.
                        }
                        else
                        {
                            // If there is any safe text before the terminating quote, emit it.
                            if (termIdx > 0)
                            {
                                string safeChunk = _buffer.ToString(0, termIdx);
                                await EmitStringChunkAsync(safeChunk, false, cancellationToken);
                            }
                            // Remove the emitted text and the terminating quote.
                            _buffer.Remove(0, termIdx + 1);
                            await EmitEndStringValueAsync(cancellationToken);
                            _state = ParserState.WaitingForCommaOrEnd;
                            continue;
                        }
                    }
                    else
                    {
                        // Process one character at a time for non-string states.
                        char c = _buffer[0];
                        _buffer.Remove(0, 1);
                        await ProcessCharacterAsync(c, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                await EmitErrorAsync($"Error processing JSON chunk: {ex.Message}", cancellationToken);
                throw;
            }
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

        /// <summary>
        /// Completes the parsing process and signals the end of the JSON stream.
        /// </summary>
        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // If we're in the middle of parsing, try to emit what we have so far
                if (_state == ParserState.InsideStringValue && _currentProperty != null)
                {
                    await EmitStringChunkAsync("", true, cancellationToken);
                }
                else if (_state == ParserState.InsidePrimitiveValue && _currentProperty != null)
                {
                    await EmitPrimitiveValueAsync(cancellationToken);
                }
                
                // Check if JSON is properly terminated
                // A proper JSON object should have reached the WaitingForObjectStart state 
                // (for empty objects) or have already seen a closing brace
                bool isCompleteJson = _state == ParserState.WaitingForObjectStart;
                
                // Report malformed JSON if we haven't seen a proper end of the JSON object
                if (!isCompleteJson)
                {
                    await EmitErrorAsync("Malformed JSON: missing closing brackets or incomplete data", cancellationToken);
                }
                
                // Signal that parsing is complete
                await _eventChannel.Writer.WriteAsync(new JsonCompleteEvent(isCompleteJson), cancellationToken);
            }
            catch (Exception ex)
            {
                await EmitErrorAsync($"Error completing JSON parsing: {ex.Message}", cancellationToken);
            }
            finally
            {
                _eventChannel.Writer.TryComplete();
            }
        }

        /// <summary>
        /// Disposes the parser resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _cts.Dispose();
            
            // Safely complete the channel, avoiding exceptions if already closed.
            _eventChannel.Writer.TryComplete();
            
            await Task.CompletedTask;
        }

        /// <summary>
        /// Finds the first occurrence of a special character (quote or escape) in a string.
        /// </summary>
        /// <param name="text">The text to search</param>
        /// <returns>Index of the first special character, or -1 if none found</returns>
        private static int FindSpecialCharInString(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\\')
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Core parsing logic: processes a single character based on the current parser state.
        /// </summary>
        private async Task ProcessCharacterAsync(char c, CancellationToken cancellationToken = default)
        {
            switch (_state)
            {
                case ParserState.WaitingForObjectStart:
                    if (c == '{')
                    {
                        _state = ParserState.WaitingForPropertyName;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        await EmitErrorAsync($"Unexpected character: {c}, expected '{{' to start JSON object", cancellationToken);
                    }
                    break;

                case ParserState.WaitingForPropertyName:
                    if (c == '"')
                    {
                        _currentPropertyName.Clear();
                        _state = ParserState.InsidePropertyName;
                    }
                    else if (c == '}')
                    {
                        // Root object has ended, transition back to initial state
                        _state = ParserState.WaitingForObjectStart;
                        // No need to emit EndObjectEvent anymore
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        await EmitErrorAsync($"Unexpected character: {c}, expected a property name", cancellationToken);
                    }
                    break;

                case ParserState.InsidePropertyName:
                    if (c == '"' && !_isEscaped)
                    {
                        _currentProperty = _currentPropertyName.ToString();
                        await EmitPropertyNameAsync(_currentProperty, cancellationToken);
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
                        
                        _currentPropertyName.Append(c);
                    }
                    break;

                case ParserState.WaitingForColon:
                    if (c == ':')
                    {
                        _state = ParserState.WaitingForValueStart;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        await EmitErrorAsync($"Unexpected character: {c}, expected ':'", cancellationToken);
                    }
                    break;

                case ParserState.WaitingForValueStart:
                    if (c == '"')
                    {
                        _isEscaped = false;
                        _state = ParserState.InsideStringValue;
                    }
                    else if (c == '{' || c == '[')
                    {
                        _nestedStructureBuffer.Clear();
                        _nestedStructureBuffer.Append(c);
                        _nestedStructureStack.Clear();
                        _nestedStructureStack.Push(c);
                        _state = ParserState.InsideNestedStructure;
                        _insideString = false;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        _primitiveValueBuffer.Clear();
                        _primitiveValueBuffer.Append(c);
                        _state = ParserState.InsidePrimitiveValue;
                    }
                    break;

                case ParserState.InsideStringValue:
                    if (c == '"' && !_isEscaped)
                    {
                        await EmitStringValueAsync("", true, cancellationToken);
                        _state = ParserState.WaitingForCommaOrEnd;
                    }
                    else if (c == '\\' && !_isEscaped)
                    {
                        _isEscaped = true;
                    }
                    else
                    {
                        if (_isEscaped)
                            _isEscaped = false;
                        
                        await EmitStringValueAsync(c.ToString(), false, cancellationToken);
                    }
                    break;

                case ParserState.InsidePrimitiveValue:
                    if (c == ',' || c == '}')
                    {
                        await EmitPrimitiveValueAsync(cancellationToken);
                        _state = c == ',' ? ParserState.WaitingForPropertyName : ParserState.WaitingForCommaOrEnd;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        _primitiveValueBuffer.Append(c);
                    }
                    break;

                case ParserState.InsideNestedStructure:
                    _nestedStructureBuffer.Append(c);
                    
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
                                    await EmitComplexValueAsync(_nestedStructureBuffer.ToString(), cancellationToken);
                                    _state = ParserState.WaitingForCommaOrEnd;
                                }
                            }
                        }
                        
                        if (_isEscaped)
                        {
                            _isEscaped = false;
                        }
                    }
                    break;

                case ParserState.WaitingForCommaOrEnd:
                    if (c == ',')
                    {
                        _state = ParserState.WaitingForPropertyName;
                    }
                    else if (c == '}')
                    {
                        // Root object has ended, transition back to initial state
                        _state = ParserState.WaitingForObjectStart;
                        // No need to emit EndObjectEvent anymore
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        await EmitErrorAsync($"Unexpected character: {c}, expected ',' or '}}'", cancellationToken);
                    }
                    break;
            }
        }

        private async Task EmitStringValueAsync(string chunk, bool isFinal, CancellationToken cancellationToken)
        {
            await _eventChannel.Writer.WriteAsync(new JsonStringValueEvent(_currentProperty, chunk, isFinal), cancellationToken);
        }

        private async Task EmitPrimitiveValueAsync(CancellationToken cancellationToken)
        {
            await _eventChannel.Writer.WriteAsync(new JsonPrimitiveValueEvent(_currentProperty, _primitiveValueBuffer.ToString()), cancellationToken);
        }

        private async Task EmitComplexValueAsync(string value, CancellationToken cancellationToken)
        {
            bool isObject = value.StartsWith("{");
            await _eventChannel.Writer.WriteAsync(new JsonComplexValueEvent(_currentProperty, value, isObject), cancellationToken);
        }

        private async Task EmitPropertyNameAsync(string propertyName, CancellationToken cancellationToken)
        {
            // Store the property name but don't emit a separate event
            _currentProperty = propertyName;
        }

        private Task EmitEndObjectAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask; // No-op
        }

        private async Task EmitErrorAsync(string message, CancellationToken cancellationToken)
        {
            await _eventChannel.Writer.WriteAsync(new JsonErrorEvent(message), cancellationToken);
        }

        // Replace the old emit methods with the new ones
        // These are just for compatibility with existing code
        private Task EmitStringChunkAsync(string chunk, bool isFinal, CancellationToken cancellationToken)
            => EmitStringValueAsync(chunk, isFinal, cancellationToken);

        private Task EmitStartStringValueAsync(CancellationToken cancellationToken)
            => Task.CompletedTask; // No-op as we don't need this event anymore

        private Task EmitEndStringValueAsync(CancellationToken cancellationToken)
            => EmitStringValueAsync("", true, cancellationToken);

        private Task EmitStartComplexValueAsync(char c, CancellationToken cancellationToken)
            => Task.CompletedTask; // No-op as we don't need this event anymore

        private Task EmitEndComplexValueAsync(string value, CancellationToken cancellationToken)
            => EmitComplexValueAsync(value, cancellationToken);
    }
}
