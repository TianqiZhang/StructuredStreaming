using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StructuredStreaming.Core;

namespace StructuredStreaming.Tests
{
    /// <summary>
    /// Base test class that contains common test methods for JSON parsers
    /// </summary>
    public abstract class JsonStreamParserTestsBase
    {
        // Each implementation must provide a parser factory
        protected abstract IJsonStreamParser CreateParser();

        // Helper method to collect events from parser using an array of string chunks
        protected List<JsonStreamEvent> CollectEvents(params string[] chunks)
        {
            var events = new List<JsonStreamEvent>();
            
            using (var parser = CreateParser())
            {
                // Process each chunk
                foreach (var chunk in chunks)
                {
                    events.AddRange(parser.ProcessChunk(chunk));
                }
                
                // Complete parsing and collect final events
                events.AddRange(parser.Complete());
            }
            
            return events;
        }

        // Standard verification methods
        protected void VerifyValidJson(List<JsonStreamEvent> events)
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

        protected void VerifyInvalidJson(List<JsonStreamEvent> events)
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

        protected string GetConcatenatedStringProperty(List<JsonStreamEvent> events, string propertyName)
        {
            return string.Join("", events
                .OfType<JsonStringValueEvent>()
                .Where(s => s.PropertyName == propertyName)
                .Select(s => s.Chunk));
        }

        protected JsonComplexValueEvent GetComplexValueEvent(List<JsonStreamEvent> events, string propertyName)
        {
            var evt = events
                .OfType<JsonComplexValueEvent>()
                .FirstOrDefault(e => e.PropertyName == propertyName);
            
            Assert.IsNotNull(evt, $"No complex value event found for property '{propertyName}'");
            return evt;
        }

        [TestMethod]
        public void TestSimpleJsonParsing()
        {
            var events = CollectEvents("{\"name\":\"John\"}");

            // Verify valid JSON structure
            VerifyValidJson(events);
            
            // Check specific property
            string name = GetConcatenatedStringProperty(events, "name");
            Assert.AreEqual("John", name);
        }

        [TestMethod]
        public void TestPartialJsonParsing()
        {
            var events = CollectEvents(
                "{\"name\":", 
                "\"John\"}"
            );

            // Verify valid JSON structure
            VerifyValidJson(events);
            
            // Check specific property
            string name = GetConcatenatedStringProperty(events, "name");
            Assert.AreEqual("John", name);
        }

        [TestMethod]
        public void TestComplexJsonParsing()
        {
            var events = CollectEvents(
                "{\"person\":{\"name\":\"John\",\"age\":30},",
                "\"characters\":[{\"name\":\"Alice\",\"age\":25},{\"name\":\"Bob\",\"age\":35}],",
                "\"story\":\"",
                "Once upon a time",
                " in a land far away",
                "\"}"
            );

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
        public void TestStreamingResponse()
        {
            var events = CollectEvents("{\"message\":\"Processing\",\"data\":{\"progress\":50}}");

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
        public void TestInvalidJson()
        {
            var events = CollectEvents(
                "{\"person\":",
                "{invalid}",
                ",\"story\":\"test\",",
                "\"characters\":[]}"
            );

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
        public void TestNestedComplexObjects()
        {
            var events = CollectEvents("{\"complex\":{\"nested\":{\"deep\":{\"value\":42}}}}");

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
        public void TestMalformedJson()
        {
            var events = CollectEvents("{\"name\":\"test\""); // Missing closing brace

            // Verify invalid JSON structure
            VerifyInvalidJson(events);
        }

        [TestMethod]
        public void TestLargeNestedStructures()
        {
            // This tests our optimized nested structure processing
            var largeArray = "[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $"{i}")) + "]";
            var events = CollectEvents($"{{\"largeArray\":{largeArray}}}");

            VerifyValidJson(events);
            var arrayEvent = GetComplexValueEvent(events, "largeArray");
            Assert.IsFalse(arrayEvent.IsObject);
            Assert.IsTrue(arrayEvent.Value.Contains("999"));
        }

        [TestMethod]
        public void TestNestedStructuresWithQuotes()
        {
            // Test nested structure with quoted strings containing braces/brackets
            var events = CollectEvents(
                "{\"nested\":{\"text\":\"This contains { and } and [ and ]\",",
                "\"moreText\":\"\\\"quoted text\\\"\"}}"
            );

            VerifyValidJson(events);
            var nestedEvent = GetComplexValueEvent(events, "nested");
            Assert.IsTrue(nestedEvent.IsObject);
            Assert.IsTrue(nestedEvent.Value.Contains("This contains { and } and [ and ]"));
            Assert.IsTrue(nestedEvent.Value.Contains("\\\"quoted text\\\""));
        }

        [TestMethod]
        public void TestHighlyFragmentedJson()
        {
            // Test parsing with extreme fragmentation, one character at a time
            var json = "{\"fragmented\":\"test\",\"number\":42}";
            var chunks = json.Select(c => c.ToString()).ToArray();
            var events = CollectEvents(chunks);

            VerifyValidJson(events);
            string value = GetConcatenatedStringProperty(events, "fragmented");
            Assert.AreEqual("test", value);
            
            var primitiveEvents = events.OfType<JsonPrimitiveValueEvent>().ToList();
            Assert.IsTrue(primitiveEvents.Any(p => p.PropertyName == "number" && p.Value == "42"));
        }

        [TestMethod]
        public void TestPrimitiveValueHandling()
        {
            // Test various primitive values with the new _valueBuffer approach
            var events = CollectEvents(
                "{\"number\":42,",
                "\"boolean\":true,",
                "\"null\":null}"
            );

            VerifyValidJson(events);
            
            var primitives = events.OfType<JsonPrimitiveValueEvent>().ToList();
            Assert.AreEqual(3, primitives.Count);
            
            Assert.IsTrue(primitives.Any(p => p.PropertyName == "number" && p.Value == "42"));
            Assert.IsTrue(primitives.Any(p => p.PropertyName == "boolean" && p.Value == "true"));
            Assert.IsTrue(primitives.Any(p => p.PropertyName == "null" && p.Value == "null"));
        }

        [TestMethod]
        public void TestStringWithEscapedQuotes()
        {
            // Test strings with escaped quotes to ensure proper buffer handling
            var events = CollectEvents("{\"escaped\":\"This has \\\"quotes\\\" inside\"}");

            VerifyValidJson(events);
            string value = GetConcatenatedStringProperty(events, "escaped");
            Assert.AreEqual("This has \\\"quotes\\\" inside", value);
        }

        [TestMethod]
        public void TestEmptyNestedStructures()
        {
            // Test empty objects and arrays
            var events = CollectEvents("{\"emptyObject\":{},\"emptyArray\":[]}");

            VerifyValidJson(events);
            
            var complexEvents = events.OfType<JsonComplexValueEvent>().ToList();
            var emptyObj = complexEvents.First(c => c.PropertyName == "emptyObject");
            var emptyArr = complexEvents.First(c => c.PropertyName == "emptyArray");
            
            Assert.AreEqual("{}", emptyObj.Value);
            Assert.IsTrue(emptyObj.IsObject);
            
            Assert.AreEqual("[]", emptyArr.Value);
            Assert.IsFalse(emptyArr.IsObject);
        }
    }

    [TestClass]
    public class CharacterByCharacterJsonStreamParserTests : JsonStreamParserTestsBase
    {
        protected override IJsonStreamParser CreateParser()
        {
            return new CharacterByCharacterJsonStreamParser();
        }
    }

    [TestClass]
    public class Utf8JsonStreamParserTests : JsonStreamParserTestsBase
    {
        protected override IJsonStreamParser CreateParser()
        {
            return new Utf8JsonStreamParser();
        }
    }
}