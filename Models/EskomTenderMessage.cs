using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Models
{
    /// <summary>
    /// Represents a tender message specifically sourced from Eskom, South Africa's national electricity utility.
    /// This class extends TenderMessageBase to include Eskom-specific properties such as source information
    /// and nullable date fields that accommodate the variability in Eskom's tender data structure.
    /// </summary>
    public class EskomTenderMessage : TenderMessageBase
    {
        /// <summary>
        /// Gets or sets the source identifier or system name from which this Eskom tender originated.
        /// This property provides additional granularity about the specific Eskom system,
        /// department, or portal that published the tender information.
        /// </summary>
        /// <value>
        /// A string representing the tender source within Eskom's systems. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the date and time when the tender was published by Eskom.
        /// This nullable property accommodates cases where publication date information
        /// may not be available or may not yet be determined for draft tenders.
        /// </summary>
        /// <value>
        /// A nullable DateTime representing when the tender was published, or null if the publication date is not available or not yet determined.
        /// </value>
        [JsonPropertyName("publishedDate")]
        public DateTime? PublishedDate { get; set; }

        /// <summary>
        /// Gets or sets the date and time when the tender submission period closes.
        /// This nullable property accommodates cases where closing date information
        /// may not be available, particularly for tenders that are still being finalized.
        /// </summary>
        /// <value>
        /// A nullable DateTime representing the tender closing deadline, or null if the closing date is not available or not yet determined.
        /// </value>
        [JsonPropertyName("closingDate")]
        public DateTime? ClosingDate { get; set; }

        /// <summary>
        /// Gets the source type identifier for Eskom tender messages.
        /// This implementation of the abstract method from TenderMessageBase returns
        /// a constant value that identifies this message as originating from Eskom.
        /// </summary>
        /// <returns>
        /// Always returns "Eskom" to identify this message type as originating from
        /// Eskom's procurement systems or related data sources.
        /// </returns>
        public override string GetSourceType() => "Eskom";
    }
}
