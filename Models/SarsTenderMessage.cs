using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Models
{
    /// <summary>
    /// Represents a tender message specifically sourced from the South African Revenue Service (SARS).
    /// This class extends TenderMessageBase to include SARS-specific properties.
    /// </summary>
    public class SarsTenderMessage : TenderMessageBase
    {
        /// <summary>
        /// Gets or sets the source identifier, which should be "SARS".
        /// </summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the date and time when the tender was published.
        /// </summary>
        [JsonPropertyName("publishedDate")]
        public DateTime? PublishedDate { get; set; }

        /// <summary>
        /// Gets or sets the date and time when the tender submission period closes.
        /// </summary>
        [JsonPropertyName("closingDate")]
        public DateTime? ClosingDate { get; set; }

        /// <summary>
        /// Gets or sets details about any briefing sessions for the tender.
        /// </summary>
        [JsonPropertyName("briefingSession")]
        public string BriefingSession { get; set; } = string.Empty;

        /// <summary>
        /// Gets the source type identifier for SARS tender messages.
        /// </summary>
        /// <returns>Always returns "SARS" to identify this message type.</returns>
        public override string GetSourceType() => "SARS";
    }
}
