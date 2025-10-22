using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Interfaces
{
    /// <summary>
    /// Defines the contract for a service that retrieves and caches configuration
    /// needed for the AI Tagging Lambda, including prompts, blocklists, and tag maps.
    /// </summary>
    public interface IConfigService
    {
        /// <summary>
        /// Retrieves the combined system and source-specific tagging prompt for a given tender source type.
        /// Fetches prompts from AWS Parameter Store and caches them.
        /// </summary>
        /// <param name="sourceType">The specific tender source (e.g., "Eskom", "SARS").</param>
        /// <returns>The combined prompt string for Bedrock tagging.</returns>
        /// <exception cref="ArgumentException">Thrown if sourceType is null or empty.</exception>
        /// <exception cref="KeyNotFoundException">Thrown if a required prompt parameter is not found.</exception>
        Task<string> GetCombinedTaggingPromptAsync(string sourceType);

        /// <summary>
        /// Retrieves the set of blocked tags.
        /// Fetches the blocklist from AWS Parameter Store and caches it.
        /// </summary>
        /// <returns>A HashSet containing the lowercase blocked tags.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the blocklist parameter is not found.</exception>
        Task<HashSet<string>> GetTagBlocklistAsync();

        /// <summary>
        /// Retrieves the dictionary used for mapping tag variations to canonical terms.
        /// Fetches the tag map from AWS Parameter Store and caches it.
        /// </summary>
        /// <returns>A Dictionary where keys are lowercase tag variations and values are the canonical terms.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the tag map parameter is not found.</exception>
        /// <exception cref="System.Text.Json.JsonException">Thrown if the tag map parameter value is invalid JSON.</exception>
        Task<Dictionary<string, string>> GetTagMapAsync();
    }
}
