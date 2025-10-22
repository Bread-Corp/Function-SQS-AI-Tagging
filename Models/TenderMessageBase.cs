using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Tender_AI_Tagging_Lambda.Converters;

namespace Tender_AI_Tagging_Lambda.Models
{
    /// <summary>
    /// Abstract base class representing the core structure of tender messages in the SQS AI Lambda system.
    /// This class provides common properties and behaviour shared across all tender message types,
    /// serving as the foundation for specific tender message implementations.
    /// </summary>
    public abstract class TenderMessageBase
    {
        /// <summary>
        /// Gets or sets the title of the tender.
        /// This represents the main heading or name of the tender opportunity.
        /// </summary>
        /// <value>
        /// A string containing the tender title. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the detailed description of the tender.
        /// This typically contains the full scope, requirements, and details of the tender opportunity.
        /// </summary>
        /// <value>
        /// A string containing the tender description. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the unique tender number or identifier.
        /// This property uses a custom converter to handle cases where the tender number
        /// might be provided as either a string or numeric value in the JSON source.
        /// </summary>
        /// <value>
        /// A string representation of the tender number. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("tenderNumber")]
        [JsonConverter(typeof(StringOrNumberConverter))]
        public string TenderNumber { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the reference code or identifier for the tender.
        /// This may be different from the tender number and could represent internal
        /// reference codes, project codes, or alternative identifiers.
        /// </summary>
        /// <value>
        /// A string containing the tender reference. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("reference")]
        public string Reference { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the target audience or sector for the tender.
        /// This describes who the tender is intended for, such as specific industries,
        /// company sizes, or qualification requirements.
        /// </summary>
        /// <value>
        /// A string describing the intended audience. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("audience")]
        public string Audience { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the office location associated with the tender.
        /// This represents the primary office or location where the tender originates
        /// or where work may be performed.
        /// </summary>
        /// <value>
        /// A string containing the office location. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("officeLocation")]
        public string OfficeLocation { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the contact email address for tender inquiries.
        /// This is the primary email contact for questions, clarifications,
        /// or submissions related to the tender.
        /// </summary>
        /// <value>
        /// A string containing the contact email address. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the physical address associated with the tender.
        /// This may represent the location where work will be performed,
        /// the tender issuing authority's address, or submission address.
        /// </summary>
        /// <value>
        /// A string containing the address information. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the province or state where the tender is located.
        /// This provides geographical context for the tender opportunity.
        /// </summary>
        /// <value>
        /// A string containing the province name. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("province")]
        public string Province { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the collection of supporting documents associated with the tender.
        /// These documents provide additional information, specifications, or requirements
        /// that bidders need to review as part of the tender process.
        /// </summary>
        /// <value>
        /// A list of SupportingDocument objects. Defaults to an empty list if no documents are provided.
        /// </value>
        [JsonPropertyName("supporting_docs")]
        public List<SupportingDocument> SupportingDocs { get; set; } = new();

        /// <summary>
        /// Gets or sets the collection of tags or keywords associated with the tender.
        /// These tags help categorize and search for tenders based on specific criteria,
        /// industries, or characteristics.
        /// </summary>
        /// <value>
        /// A list of string tags. Defaults to an empty list if no tags are provided.
        /// </value>
        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new();

        /// <summary>
        /// Gets or sets the AI-generated summary for the tender.
        /// This property is not populated during deserialization, but is included in serialization when pushing to the write queue.
        /// </summary>
        /// <value>
        /// A string containing the summary of the tender. Defaults to null if not set.
        /// </value>
        [JsonPropertyName("ai_summary")]
        public string? AISummary { get; set; }

        /// <summary>
        /// Gets the source type identifier for this tender message.
        /// This abstract method must be implemented by derived classes to specify
        /// the specific type or origin of the tender message.
        /// </summary>
        /// <returns>
        /// A string that uniquely identifies the source type of this tender message.
        /// The format and values are defined by the implementing classes.
        /// </returns>
        public abstract string GetSourceType();
    }
}
