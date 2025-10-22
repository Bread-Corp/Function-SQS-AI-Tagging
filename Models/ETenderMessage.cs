using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Models
{
    /// <summary>
    /// Represents a tender message specifically sourced from the eTenders platform.
    /// This class extends TenderMessageBase to include eTenders-specific properties
    /// such as unique identifiers, status information, and important dates for
    /// tender lifecycle management.
    /// </summary>
    public class ETenderMessage : TenderMessageBase
    {
        /// <summary>
        /// Gets or sets the unique numerical identifier for the tender in the eTenders system.
        /// This ID is typically assigned by the eTenders platform when a tender is first
        /// created and serves as the primary key for the tender record.
        /// </summary>
        /// <value>
        /// An integer representing the eTenders system ID. Defaults to 0 if not specified.
        /// </value>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Gets or sets the current status of the tender in the eTenders system.
        /// This indicates the current stage of the tender in its lifecycle, from
        /// initial publication through to closure and award.
        /// </summary>
        /// <value>
        /// A string representing the tender status. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the date and time when the tender was officially published
        /// and made available for public viewing and submissions.
        /// </summary>
        /// <value>
        /// A DateTime representing when the tender was published. Defaults to DateTime's default value if not specified.
        /// </value>
        [JsonPropertyName("datePublished")]
        public DateTime DatePublished { get; set; }

        /// <summary>
        /// Gets or sets the date and time when the tender submission period closes.
        /// After this date and time, no new submissions will be accepted for the tender.
        /// </summary>
        /// <value>
        /// A DateTime representing the tender closing deadline. Defaults to DateTime's default value if not specified.
        /// </value>
        [JsonPropertyName("dateClosing")]
        public DateTime DateClosing { get; set; }

        /// <summary>
        /// Gets or sets the direct URL to view the full tender details on the eTenders platform.
        /// This provides a direct link that users can follow to access the complete
        /// tender information, submit bids, or download additional documents.
        /// </summary>
        /// <value>
        /// A string containing the tender URL. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the collection of supporting documents specifically for this eTender.
        /// This property shadows the base class SupportingDocs property to ensure proper
        /// JSON serialization with the eTenders-specific property name while maintaining
        /// the same data structure and functionality.
        /// </summary>
        /// <value>
        /// A list of SupportingDocument objects specific to this eTender. Defaults to an empty list if not specified.
        /// </value>
        [JsonPropertyName("supportingDocs")]
        public new List<SupportingDocument> SupportingDocs { get; set; } = new();

        /// <summary>
        /// Gets the source type identifier for eTenders messages.
        /// This implementation of the abstract method from TenderMessageBase returns
        /// a constant value that identifies this message as originating from the eTenders platform.
        /// </summary>
        /// <returns>
        /// Always returns "eTenders" to identify this message type as originating from
        /// the eTenders platform or compatible systems.
        /// </returns>
        public override string GetSourceType() => "eTenders";
    }
}
