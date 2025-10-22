using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Models
{
    /// <summary>
    /// Represents a tender message specifically sourced from Transnet, South Africa's national transport utility.
    /// This class extends TenderMessageBase to include Transnet-specific properties such as institution details,
    /// tender categorization, and nullable date fields that accommodate the variability in Transnet's tender data structure.
    /// </summary>
    public class TransnetTenderMessage : TenderMessageBase
    {
        /// <summary>
        /// Gets or sets the name of the institution issuing the tender.
        /// This represents the specific Transnet division or subsidiary responsible for the tender.
        /// </summary>
        [JsonPropertyName("institution")]
        public string Institution { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the category of the tender.
        /// This categorizes the tender based on the type of goods, services, or works required.
        /// </summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the type of tender.
        /// This specifies the procurement method or tender type used by Transnet.
        /// </summary>
        [JsonPropertyName("tenderType")]
        public string TenderType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the location where the service or work will be performed.
        /// This indicates the geographical location or site where the tender requirements will be fulfilled.
        /// </summary>
        [JsonPropertyName("location")]
        public string Location { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the name of the contact person for tender inquiries.
        /// This is the designated person responsible for handling questions and communications about the tender.
        /// </summary>
        [JsonPropertyName("contactPerson")]
        public string ContactPerson { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the source identifier or system name from which this Transnet tender originated.
        /// Maps to the 'source' field from Python model.
        /// </summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the date and time when the tender was published by Transnet.
        /// Maps to the 'publishedDate' field from Python model.
        /// </summary>
        [JsonPropertyName("publishedDate")]
        public DateTime? PublishedDate { get; set; }

        /// <summary>
        /// Gets or sets the date and time when the tender submission period closes.
        /// Maps to the 'closingDate' field from Python model.
        /// </summary>
        [JsonPropertyName("closingDate")]
        public DateTime? ClosingDate { get; set; }

        /// <summary>
        /// Override supporting docs to handle Python naming convention.
        /// </summary>
        [JsonPropertyName("supporting_docs")]
        public new List<SupportingDocument> SupportingDocs { get; set; } = new();

        /// <summary>
        /// Gets the source type identifier for Transnet tender messages.
        /// </summary>
        /// <returns>
        /// Always returns "Transnet" to identify this message type.
        /// </returns>
        public override string GetSourceType() => "Transnet";
    }
}
