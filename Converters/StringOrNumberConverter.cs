using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Converters
{
    /// <summary>
    /// A custom JSON converter that handles conversion between JSON values (strings, numbers, or null) 
    /// and .NET string objects during serialization and deserialization.
    /// This converter is particularly useful when dealing with APIs or data sources that may 
    /// inconsistently represent the same field as either a string or a number.
    /// </summary>
    public class StringOrNumberConverter : JsonConverter<string>
    {
        /// <summary>
        /// Reads and converts a JSON value to a string during deserialization.
        /// Handles three JSON token types: String, Number, and Null.
        /// </summary>
        /// <param name="reader">The Utf8JsonReader to read the JSON value from</param>
        /// <param name="typeToConvert">The target type to convert to (always string for this converter)</param>
        /// <param name="options">Serializer options that can affect conversion behavior</param>
        /// <returns>
        /// - The string value if the JSON token is a string
        /// - The decimal number converted to string if the JSON token is a number
        /// - null if the JSON token is null
        /// </returns>
        /// <exception cref="JsonException">
        /// Thrown when the JSON token type is not supported (not String, Number, or Null)
        /// </exception>
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Use pattern matching to handle different JSON token types
            return reader.TokenType switch
            {
                // Direct string value - return as-is
                JsonTokenType.String => reader.GetString(),

                // Numeric value - convert to decimal first for precision, then to string
                // Using GetDecimal() ensures we don't lose precision for large numbers
                JsonTokenType.Number => reader.GetDecimal().ToString(),

                // Null value - return null to maintain nullability
                JsonTokenType.Null => null,

                // Any other token type is not supported - throw descriptive exception
                _ => throw new JsonException($"Cannot convert {reader.TokenType} to string")
            };
        }

        /// <summary>
        /// Writes a string value to JSON during serialization.
        /// Always serializes the string value as a JSON string, regardless of whether 
        /// the original source was a string or number.
        /// </summary>
        /// <param name="writer">The Utf8JsonWriter to write the JSON value to</param>
        /// <param name="value">The string value to serialize</param>
        /// <param name="options">Serializer options that can affect serialization behavior</param>
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            // Always write as a JSON string value, maintaining consistency in output format
            writer.WriteStringValue(value);
        }
    }
}
