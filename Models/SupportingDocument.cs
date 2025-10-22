using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Models
{
    /// <summary>
    /// Represents a supporting document associated with a tender in the SQS AI Lambda system.
    /// This class encapsulates the essential information needed to identify and access
    /// documents that provide additional details, specifications, or requirements for tender opportunities.
    /// </summary>
    public class SupportingDocument
    {
        /// <summary>
        /// Gets or sets the name or title of the supporting document.
        /// This represents the display name, filename, or descriptive title that identifies
        /// the document to users and systems.
        /// </summary>
        /// <value>
        /// A string containing the document name. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the URL where the supporting document can be accessed or downloaded.
        /// This should be a valid, accessible web address that provides direct access to the document content.
        /// </summary>
        /// <value>
        /// A string containing the document URL. Defaults to an empty string if not specified.
        /// </value>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }
}
