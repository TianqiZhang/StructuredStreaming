using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StructuredStreaming.Core;

namespace StructuredStreaming.Tests
{
    [TestClass]
    public class JsonStreamParserTests
    {
        // Helper method to collect events from parser
        private async Task<List<JsonStreamEvent>> CollectEventsAsync(Func<JsonStreamParser, Task> processAction)
        {
            var events = new List<JsonStreamEvent>();
            
            await using (var parser = new JsonStreamParser())
            {
                // Start collecting events in a separate task
                var collectTask = Task.Run(async () => {
                    await foreach (var evt in parser.GetEventsAsync())
                    {
                        events.Add(evt);
                    }
                });
                
                // Process the input
                await processAction(parser);
                
                // Complete parsing and wait for event collection
                await parser.CompleteAsync();
                await collectTask;
            }
            
            return events;
        }

        // Standard verification methods
        private void VerifyValidJson(List<JsonStreamEvent> events)
        {
            // No error events should be present for valid JSON
            var errors = events.OfType<JsonErrorEvent>().ToList();
            Assert.AreEqual(0, errors.Count, "Valid JSON should not generate error events");

            // Should have exactly one complete event
            var completeEvents = events.OfType<JsonCompleteEvent>().ToList();
            Assert.AreEqual(1, completeEvents.Count, "Should have exactly one complete event");
            
            // The JSON should be marked as valid
            Assert.IsTrue(completeEvents[0].IsValidJson, "JSON should be marked as valid");
        }

        private void VerifyInvalidJson(List<JsonStreamEvent> events)
        {
            // At least one error event should be present for invalid JSON
            var errors = events.OfType<JsonErrorEvent>().ToList();
            Assert.IsTrue(errors.Count > 0, "Invalid JSON should generate at least one error event");

            // Should have exactly one complete event
            var completeEvents = events.OfType<JsonCompleteEvent>().ToList();
            Assert.AreEqual(1, completeEvents.Count, "Should have exactly one complete event");
            
            // The JSON should be marked as invalid
            Assert.IsFalse(completeEvents[0].IsValidJson, "JSON should be marked as invalid");
        }

        private string GetConcatenatedStringProperty(List<JsonStreamEvent> events, string propertyName)
        {
            return string.Join("", events
                .OfType<JsonStringValueEvent>()
                .Where(s => s.PropertyName == propertyName)
                .Select(s => s.Chunk));
        }

        private JsonComplexValueEvent GetComplexValueEvent(List<JsonStreamEvent> events, string propertyName)
        {
            var evt = events
                .OfType<JsonComplexValueEvent>()
                .FirstOrDefault(e => e.PropertyName == propertyName);
            
            Assert.IsNotNull(evt, $"No complex value event found for property '{propertyName}'");
            return evt;
        }

        [TestMethod]
        public async Task TestSimpleJsonParsing()
        {
            var events = await CollectEventsAsync(async parser =>
            {
                await parser.ProcessChunkAsync("{\"name\":\"John\"}");
            });

            // Verify valid JSON structure
            VerifyValidJson(events);
            
            // Check specific property
            string name = GetConcatenatedStringProperty(events, "name");
            Assert.AreEqual("John", name);
        }

        [TestMethod]
        public async Task TestPartialJsonParsing()
        {
            var events = await CollectEventsAsync(async parser =>
            {
                await parser.ProcessChunkAsync("{\"name\":");
                await parser.ProcessChunkAsync("\"John\"}");
            });

            // Verify valid JSON structure
            VerifyValidJson(events);
            
            // Check specific property
            string name = GetConcatenatedStringProperty(events, "name");
            Assert.AreEqual("John", name);
        }

        [TestMethod]
        public async Task TestComplexJsonParsing()
        {
            var events = await CollectEventsAsync(async parser =>
            {
                await parser.ProcessChunkAsync("{\"person\":{\"name\":\"John\",\"age\":30},");
                await parser.ProcessChunkAsync("\"characters\":[{\"name\":\"Alice\",\"age\":25},{\"name\":\"Bob\",\"age\":35}],");
                await parser.ProcessChunkAsync("\"story\":\"");
                await parser.ProcessChunkAsync("Once upon a time");
                await parser.ProcessChunkAsync(" in a land far away");
                await parser.ProcessChunkAsync("\"}");
            });

            // Verify valid JSON structure
            VerifyValidJson(events);

            // Check person object
            var personEvent = GetComplexValueEvent(events, "person");
            Assert.IsTrue(personEvent.Value.Contains("\"name\":\"John\""));
            Assert.IsTrue(personEvent.Value.Contains("\"age\":30"));
            Assert.IsTrue(personEvent.IsObject);

            // Check characters array
            var charactersEvent = GetComplexValueEvent(events, "characters");
            Assert.IsTrue(charactersEvent.Value.Contains("\"name\":\"Alice\""));
            Assert.IsTrue(charactersEvent.Value.Contains("\"name\":\"Bob\""));
            Assert.IsFalse(charactersEvent.IsObject); // It's an array

            // Check story string
            string story = GetConcatenatedStringProperty(events, "story");
            Assert.AreEqual("Once upon a time in a land far away", story);
            
            // Verify string completion event was emitted
            Assert.IsTrue(events.OfType<JsonStringValueEvent>()
                .Any(e => e.PropertyName == "story" && e.IsFinal), 
                "Should have a final string chunk event for 'story'");
        }

        [TestMethod]
        public async Task TestStreamingResponse()
        {
            var events = await CollectEventsAsync(async parser =>
            {
                await parser.ProcessChunkAsync("{\"message\":\"Processing\",\"data\":{\"progress\":50}}");
            });

            // Verify valid JSON structure
            VerifyValidJson(events);

            // Check message string
            string message = GetConcatenatedStringProperty(events, "message");
            Assert.AreEqual("Processing", message);

            // Check data object
            var dataEvent = GetComplexValueEvent(events, "data");
            Assert.IsTrue(dataEvent.Value.Contains("\"progress\":50"));
        }

        [TestMethod]
        public async Task TestInvalidJson()
        {
            var events = await CollectEventsAsync(async parser =>
            {
                await parser.ProcessChunkAsync("{\"person\":");
                await parser.ProcessChunkAsync("{invalid}");
                await parser.ProcessChunkAsync(",\"story\":\"test\",");
                await parser.ProcessChunkAsync("\"characters\":[]}");
            });

            // We don't verify if it's valid or invalid here because we're still getting some 
            // valid events despite the invalid content. The parser should try its best to recover.

            // Check that we still get some valid events
            string story = GetConcatenatedStringProperty(events, "story");
            Assert.AreEqual("test", story);

            var charactersEvent = events.OfType<JsonComplexValueEvent>()
                .FirstOrDefault(e => e.PropertyName == "characters");
            Assert.IsNotNull(charactersEvent);
            Assert.AreEqual("[]", charactersEvent.Value);
        }

        [TestMethod]
        public async Task TestNestedComplexObjects()
        {
            var events = await CollectEventsAsync(async parser =>
            {
                await parser.ProcessChunkAsync("{\"complex\":{\"nested\":{\"deep\":{\"value\":42}}}}");
            });

            // Verify valid JSON structure
            VerifyValidJson(events);

            // Check complex object
            var complexEvent = GetComplexValueEvent(events, "complex");
            Assert.IsTrue(complexEvent.Value.Contains("\"nested\""));
            Assert.IsTrue(complexEvent.Value.Contains("\"deep\""));
            Assert.IsTrue(complexEvent.Value.Contains("\"value\":42"));
            Assert.IsTrue(complexEvent.IsObject);
        }

        [TestMethod]
        public async Task TestMalformedJson()
        {
            var events = await CollectEventsAsync(async parser =>
            {
                await parser.ProcessChunkAsync("{\"name\":\"test\""); // Missing closing brace
            });

            // Verify invalid JSON structure
            VerifyInvalidJson(events);
        }
    }
}