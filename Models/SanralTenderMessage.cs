using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Models
{
    /// <summary>
    /// Represents a tender message specifically sourced from the South African National Roads Agency (SANRAL).
    /// This class extends TenderMessageBase to include SANRAL-specific properties.
    /// </summary>
    public class SanralTenderMessage : TenderMessageBase
    {
        /// <summary>
        /// Gets or sets the source identifier, which should be "SANRAL".
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
        /// Gets or sets the category of the tender (e.g., "Other Projects").
        /// </summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the geographical region for the tender.
        /// </summary>
        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the full text of the tender notice.
        /// </summary>
        [JsonPropertyName("fullNoticeText")]
        public string FullNoticeText { get; set; } = string.Empty;

        /// <summary>
        /// Gets the source type identifier for SANRAL tender messages.
        /// </summary>
        /// <returns>Always returns "SANRAL" to identify this message type.</returns>
        public override string GetSourceType() => "SANRAL";
    }
}
