using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tender_AI_Tagging_Lambda.Models;

namespace Tender_AI_Tagging_Lambda.Interfaces
{
    /// <summary>
    /// Defines the contract for a service that generates and cleans relevant tags
    /// for a tender message using AI and predefined rules.
    /// </summary>
    public interface ITaggingService
    {
        /// <summary>
        /// Clears existing tags, generates new tags using Bedrock and fallback logic,
        /// applies quality rules (blocklist, mapping, normalization), and returns the final list.
        /// </summary>
        /// <param name="tenderMessage">The tender message object to process.</param>
        /// <returns>A sorted list of cleaned, relevant tags for the tender.</returns>
        Task<List<string>> GenerateAndCleanTagsAsync(TenderMessageBase tenderMessage);
    }
}
