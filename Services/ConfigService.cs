using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Logging;
using Tender_AI_Tagging_Lambda.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Services
{
    /// <summary>
    /// Service responsible for fetching and caching configuration from AWS Parameter Store.
    /// Manages tagging prompts, blocklist, and tag map.
    /// </summary>
    public class ConfigService : IConfigService
    {
        private readonly IAmazonSimpleSystemsManagement _ssmClient;
        private readonly ILogger<ConfigService> _logger;

        // Parameter Store paths and keys
        private const string PromptBasePath = "/TenderSummary/Prompts/";
        private const string TaggingSystemPromptSuffix = "TaggingSystem";
        private const string TaggingSourcePromptPrefix = "Tagging"; // e.g., TaggingEskom
        private const string BlocklistParamName = "/tenders/ai-processor/tag-blocklist";
        private const string TagMapParamName = "/tenders/ai-processor/tag-map";
        private const string MasterTagListParamName = "/tenders/ai-processor/master-tag-categories"; // Parameter name for Master Tag List

        // Static cache using ConcurrentDictionary for thread safety
        private static readonly ConcurrentDictionary<string, object> _configCache = new();
        private const string BlocklistCacheKey = "config_Blocklist";
        private const string TagMapCacheKey = "config_TagMap";
        private const string MasterTagListCacheKey = "config_MasterTagList"; // Cache key for Master Tag List

        public ConfigService(IAmazonSimpleSystemsManagement ssmClient, ILogger<ConfigService> logger)
        {
            _ssmClient = ssmClient ?? throw new ArgumentNullException(nameof(ssmClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        public async Task<string> GetCombinedTaggingPromptAsync(string sourceType)
        {
            if (string.IsNullOrWhiteSpace(sourceType))
            {
                throw new ArgumentException("Source type cannot be null or empty.", nameof(sourceType));
            }

            string systemPromptKey = $"prompt_{TaggingSystemPromptSuffix}";
            string sourcePromptKey = $"prompt_{TaggingSourcePromptPrefix}{sourceType.ToLower()}"; // e.g., prompt_Taggingeskom

            // Fetch system prompt (cache handled internally)
            string systemPrompt = await EnsurePromptCachedAsync(TaggingSystemPromptSuffix, systemPromptKey);

            // Fetch source-specific prompt (cache handled internally)
            string sourcePrompt = await EnsurePromptCachedAsync($"{TaggingSourcePromptPrefix}{sourceType.ToLower()}", sourcePromptKey);

            // Fetch master tag list (cache handled internally)
            var masterTags = await GetMasterCategoryTagsAsync();
            string masterTagListString = string.Join(", ", masterTags.Select(t => $"\"{t}\""));

            // Inject master tag list into the combined prompt
            string combinedPrompt = $"{systemPrompt}\n\nMASTER TAG LIST: [{masterTagListString}]\n\n{sourcePrompt}";

            _logger.LogDebug("Combined tagging prompt retrieved for source type: {SourceType}", sourceType);
            return combinedPrompt;
        }

        /// <inheritdoc/>
        public async Task<HashSet<string>> GetTagBlocklistAsync()
        {
            if (_configCache.TryGetValue(BlocklistCacheKey, out var cachedBlocklist) && cachedBlocklist is HashSet<string> blocklistSet)
            {
                _logger.LogDebug("Tag blocklist found in cache.");
                return blocklistSet;
            }

            _logger.LogInformation("Tag blocklist not in cache. Fetching from Parameter Store: {ParameterName}", BlocklistParamName);
            string blocklistString = await FetchParameterValueAsync(BlocklistParamName);

            // Parse comma-separated string into a HashSet of lowercase strings
            var parsedBlocklist = new HashSet<string>(
                blocklistString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                               .Select(s => s.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            _configCache.TryAdd(BlocklistCacheKey, parsedBlocklist);
            _logger.LogInformation("Successfully fetched and cached tag blocklist. Count: {Count}", parsedBlocklist.Count);
            return parsedBlocklist;
        }

        /// <inheritdoc/>
        public async Task<Dictionary<string, string>> GetTagMapAsync()
        {
            if (_configCache.TryGetValue(TagMapCacheKey, out var cachedMap) && cachedMap is Dictionary<string, string> tagMapDict)
            {
                _logger.LogDebug("Tag map found in cache.");
                return tagMapDict;
            }

            _logger.LogInformation("Tag map not in cache. Fetching from Parameter Store: {ParameterName}", TagMapParamName);
            string tagMapJson = await FetchParameterValueAsync(TagMapParamName);

            try
            {
                // Deserialize JSON string into a Dictionary, ensuring keys are lowercase
                var parsedMap = JsonSerializer.Deserialize<Dictionary<string, string>>(tagMapJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true // Handle potential casing issues in the stored JSON keys
                });

                if (parsedMap == null)
                {
                    _logger.LogWarning("Deserializing tag map JSON resulted in null. Using empty map.");
                    parsedMap = new Dictionary<string, string>();
                }

                // Ensure keys are lowercase for consistent lookups
                var finalMap = new Dictionary<string, string>(parsedMap.ToDictionary(kvp => kvp.Key.ToLowerInvariant(), kvp => kvp.Value),
                                                               StringComparer.OrdinalIgnoreCase);


                _configCache.TryAdd(TagMapCacheKey, finalMap);
                _logger.LogInformation("Successfully fetched and cached tag map. Count: {Count}", finalMap.Count);
                return finalMap;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse Tag Map JSON from Parameter Store: {ParameterName}. Value was: {Value}", TagMapParamName, tagMapJson);
                throw; // Re-throw as this is a critical configuration error
            }
        }

        /// <inheritdoc/>
        public async Task<List<string>> GetMasterCategoryTagsAsync()
        {
            if (_configCache.TryGetValue(MasterTagListCacheKey, out var cachedList) && cachedList is List<string> tagList)
            {
                _logger.LogDebug("Master category tag list found in cache.");
                return tagList;
            }

            _logger.LogInformation("Master category tag list not in cache. Fetching from Parameter Store: {ParameterName}", MasterTagListParamName);
            string listString = await FetchParameterValueAsync(MasterTagListParamName);

            // Parse comma-separated string into a List<string>
            var parsedList = listString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                       .ToList();

            _configCache.TryAdd(MasterTagListCacheKey, parsedList);
            _logger.LogInformation("Successfully fetched and cached master category tag list. Count: {Count}", parsedList.Count);
            return parsedList;
        }

        /// <summary>
        /// Helper to ensure a specific prompt is cached, fetching if necessary.
        /// </summary>
        private async Task<string> EnsurePromptCachedAsync(string promptNameSuffix, string cacheKey)
        {
            if (_configCache.TryGetValue(cacheKey, out var cachedPrompt) && cachedPrompt is string promptStr)
            {
                _logger.LogDebug("Prompt found in cache for key: {CacheKey}", cacheKey);
                return promptStr;
            }

            string parameterName = $"{PromptBasePath}{promptNameSuffix}";
            _logger.LogInformation("Prompt not found in cache for key: {CacheKey}. Fetching from Parameter Store: {ParameterName}", cacheKey, parameterName);

            string promptValue = await FetchParameterValueAsync(parameterName);
            _configCache.TryAdd(cacheKey, promptValue);
            _logger.LogInformation("Successfully fetched and cached prompt for key: {CacheKey}", cacheKey);
            return promptValue;
        }

        /// <summary>
        /// Generic helper to fetch a parameter value from SSM.
        /// </summary>
        private async Task<string> FetchParameterValueAsync(string parameterName)
        {
            try
            {
                var request = new GetParameterRequest
                {
                    Name = parameterName,
                    WithDecryption = false // Assuming plain text strings
                };
                var response = await _ssmClient.GetParameterAsync(request);

                if (response.Parameter == null || string.IsNullOrEmpty(response.Parameter.Value))
                {
                    _logger.LogError("Parameter {ParameterName} was found but has no value.", parameterName);
                    throw new KeyNotFoundException($"Parameter '{parameterName}' retrieved from Parameter Store has no value.");
                }
                return response.Parameter.Value;
            }
            catch (ParameterNotFoundException ex)
            {
                _logger.LogError(ex, "Required configuration parameter not found in Parameter Store: {ParameterName}", parameterName);
                throw new KeyNotFoundException($"Required configuration parameter '{parameterName}' not found in AWS Parameter Store.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch parameter {ParameterName} from Parameter Store.", parameterName);
                throw; // Re-throw for general failures
            }
        }
    }
}
