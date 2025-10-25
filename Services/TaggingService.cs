using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Logging;
using Tender_AI_Tagging_Lambda.Interfaces;
using Tender_AI_Tagging_Lambda.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Services
{
    /// <summary>
    /// Service responsible for generating content-relevant tags for tender messages
    /// using AWS Bedrock (Claude 3 Sonnet), fallback logic, and quality rules.
    /// </summary>
    public class TaggingService : ITaggingService
    {
        private readonly IAmazonBedrockRuntime _bedrockClient;
        private readonly IConfigService _configService;
        private readonly ILogger<TaggingService> _logger;

        // Bedrock configuration
        private const string ModelId = "anthropic.claude-3-sonnet-20240229-v1:0";
        private const int MaxRetryAttempts = 6; // Increased to 6 total attempts
        private const int BaseDelayMs = 1500;   // 1.5 second base delay
        private const int MaxTotalTags = 10; // The hard limit for total tags

        // Concurrency control for Bedrock
        private static readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1); // Max 1 concurrent request per Lambda

        public TaggingService(IAmazonBedrockRuntime bedrockClient, IConfigService configService, ILogger<TaggingService> logger)
        {
            _bedrockClient = bedrockClient ?? throw new ArgumentNullException(nameof(bedrockClient));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        /// <summary>
        /// This method is rewritten to follow the new 10-tag limit and hybrid generation logic.
        /// </summary>
        public async Task<List<string>> GenerateAndCleanTagsAsync(TenderMessageBase tenderMessage)
        {
            // Step 1: Clear Existing Tags
            tenderMessage.Tags.Clear(); // Start fresh
            _logger.LogDebug("Cleared existing metadata tags for TenderNumber: {TenderNumber}", tenderMessage.TenderNumber);

            // Step 2: Get All Configuration
            var blocklist = await _configService.GetTagBlocklistAsync();
            var tagMap = await _configService.GetTagMapAsync();

            // GetCombinedTaggingPromptAsync() now also fetches the master tag list and injects it.
            var combinedPrompt = await _configService.GetCombinedTaggingPromptAsync(tenderMessage.GetSourceType());

            // Step 3: Generate and Clean Fallback Tags First
            var fallbackTags = ExtractFallbackTags(tenderMessage);
            var cleanedFallbackTags = ApplyQualityGatekeeper(fallbackTags, blocklist, tagMap);
            var finalTagSet = new HashSet<string>(cleanedFallbackTags, StringComparer.OrdinalIgnoreCase);

            _logger.LogDebug("Generated {Count} clean fallback tags for {TenderNumber}.", finalTagSet.Count, tenderMessage.TenderNumber);

            // Step 4: Check Quota and Call Bedrock if Needed
            int tagsNeeded = MaxTotalTags - finalTagSet.Count;

            if (tagsNeeded > 0)
            {
                _logger.LogInformation("Have {CurrentCount} fallback tags, need {TagsNeeded} more from AI.", finalTagSet.Count, tagsNeeded);

                List<string> rawAiTags = new List<string>();

                try
                {
                    string inputText = PrepareInputText(tenderMessage);

                    if (!string.IsNullOrWhiteSpace(inputText))
                    {
                        // Wait for our turn to call Bedrock
                        await _rateLimitSemaphore.WaitAsync();

                        try
                        {
                            // Pass the number of tags we need to the Bedrock call
                            string rawTagString = await ExecuteBedrockRequestWithRetryAsync(combinedPrompt, inputText, tagsNeeded, tenderMessage.TenderNumber ?? "Unknown");
                            rawAiTags = ParseBedrockTagResponse(rawTagString);
                            _logger.LogDebug("Bedrock generated {Count} raw tags for {TenderNumber}", rawAiTags.Count, tenderMessage.TenderNumber);
                        }
                        finally
                        {
                            _rateLimitSemaphore.Release(); // Always release the semaphore
                        }

                        // Step 5: Clean AI Tags and Add to Final Set
                        var cleanedAiTags = ApplyQualityGatekeeper(rawAiTags, blocklist, tagMap);

                        foreach (var aiTag in cleanedAiTags)
                        {
                            if (finalTagSet.Count >= MaxTotalTags)
                            {
                                break; // Stop adding if we've hit the 10-tag limit
                            }
                            finalTagSet.Add(aiTag); // HashSet handles all uniqueness
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No suitable text found for Bedrock tagging for TenderNumber: {TenderNumber}", tenderMessage.TenderNumber);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Bedrock tag generation failed for TenderNumber: {TenderNumber}. Proceeding with fallback tags only.", tenderMessage.TenderNumber);
                    // Continue with just the fallback tags
                }
            }
            else
            {
                _logger.LogInformation("Fallback tag count ({Count}) met or exceeded limit. Skipping Bedrock.", finalTagSet.Count);
            }

            // Step 6: Finalize, Sort, and Return
            var finalList = finalTagSet.Take(MaxTotalTags).ToList(); // Enforce hard limit just in case
            finalList.Sort();

            _logger.LogInformation("Generated {Count} final tags for TenderNumber: {TenderNumber}", finalList.Count, tenderMessage.TenderNumber);
            return finalList;
        }

        /// <summary>
        /// Selects the best text content from the tender for AI analysis.
        /// </summary>
        private string PrepareInputText(TenderMessageBase tender)
        {
            // Prioritize fields for tagging context
            var textBuilder = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(tender.Title))
                textBuilder.AppendLine($"Title: {tender.Title}");

            if (!string.IsNullOrWhiteSpace(tender.Description))
                textBuilder.AppendLine($"Description: {tender.Description}");
            
            // Include summary if it exists and might provide context
            if (!string.IsNullOrWhiteSpace(tender.AISummary))
                textBuilder.AppendLine($"Summary: {tender.AISummary}");
            
            // Include fullNoticeText for SANRAL as it's often detailed
            if (tender is SanralTenderMessage sanral && !string.IsNullOrWhiteSpace(sanral.FullNoticeText))
                textBuilder.AppendLine($"Full Notice Text: {sanral.FullNoticeText}");

            // Basic truncation
            const int MaxLength = 10000;
            string combinedText = textBuilder.ToString();
            return combinedText.Length > MaxLength ? combinedText.Substring(0, MaxLength) + "..." : combinedText;
        }

        /// <summary>
        /// Executes the Bedrock request with retry logic.
        /// Method signature updated to accept 'tagsNeeded'
        /// </summary>
        private async Task<string> ExecuteBedrockRequestWithRetryAsync(string combinedPrompt, string inputText, int tagsNeeded, string tenderNumber)
        {
            var attempt = 0;
            while (attempt < MaxRetryAttempts)
            {
                attempt++;

                try
                {
                    _logger.LogDebug("Bedrock tagging request attempt {Attempt}/{MaxAttempts} - TenderNumber: {TenderNumber}", attempt, MaxRetryAttempts, tenderNumber);

                    // Calculate the number of additional tags to ask for.
                    // The prompt asks for 1 Master Tag + (N-1) additional tags.
                    int additionalTagsToGenerate = Math.Max(0, tagsNeeded - 1);

                    // All static instructions are now in the `combinedPrompt` from Parameter Store.
                    // We only append the dynamic task instructions and the tender text itself.
                    string finalTaskInstruction = $"""

                    ---
                    DYNAMIC TASK:
                    1.  You MUST select 1 tag from the "MASTER TAG LIST" (provided in the prompt above).
                    2.  You MUST generate {additionalTagsToGenerate} additional, specific keywords from the "Tender Text" below.
                    3.  You MUST return a total of {tagsNeeded} tags.
                    4.  Your response MUST be ONLY a simple, comma-separated list of these {tagsNeeded} tags (the 1 master tag + the {additionalTagsToGenerate} generated keywords).
                    ---

                    Tender Text:
                    {inputText}
                    """;

                    // The combinedPrompt already contains System + Master List + Source-Specific instructions.
                    // We just append the final task and the data.
                    string fullPrompt = combinedPrompt + finalTaskInstruction;

                    var payload = new
                    {
                        anthropic_version = "bedrock-2023-05-31",
                        max_tokens = 450, // Tagging requires fewer tokens than summarization
                        temperature = 0.2, // Lower temperature for more deterministic tags
                        messages = new[]
                        {
                            new
                            {
                                role = "user",
                                content = new[]
                                {
                                    new
                                    {
                                        type = "text",
                                        text = fullPrompt // Full prompt with instructions and data
                                    }
                                }
                            }
                        }
                    };

                    var request = new InvokeModelRequest
                    {
                        ModelId = ModelId,
                        ContentType = "application/json",
                        Accept = "application/json",
                        Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))
                    };

                    var response = await _bedrockClient.InvokeModelAsync(request);
                    using var responseStream = new StreamReader(response.Body);
                    var responseText = await responseStream.ReadToEndAsync();
                    var tagsString = ParseClaudeResponse(responseText); // Reuse parsing logic

                    _logger.LogDebug("Bedrock tagging request successful on attempt {Attempt} - TenderNumber: {TenderNumber}", attempt, tenderNumber);
                    return tagsString;
                }
                catch (ThrottlingException)
                {
                    if (attempt == MaxRetryAttempts) throw;
                    var delay = CalculateBackoffDelay(attempt);
                    _logger.LogWarning("Bedrock throttling detected on tagging attempt {Attempt}, retrying in {Delay}ms for {TenderNumber}", attempt, delay, tenderNumber);
                    await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Bedrock tagging request failed with non-retryable error on attempt {Attempt} for {TenderNumber}", attempt, tenderNumber);
                    throw; // Re-throw non-throttling errors
                }
            }
            // Should not be reached if MaxRetryAttempts > 0
            throw new InvalidOperationException($"Max retry attempts exceeded for Bedrock tagging on tender {tenderNumber}");
        }

        /// <summary>
        /// Parses the comma-separated string (or similar format) from Bedrock's response.
        /// </summary>
        private List<string> ParseBedrockTagResponse(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
            {
                return new List<string>();
            }
            // Split by comma, trim whitespace, remove empty entries
            return rawResponse.Split(',')
                              .Select(tag => tag.Trim())
                              .Where(tag => !string.IsNullOrEmpty(tag))
                              .ToList();
        }

        /// <summary>
        /// Extracts fallback tags from structured fields of the tender message.
        /// </summary>
        private List<string> ExtractFallbackTags(TenderMessageBase tender)
        {
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Use HashSet for initial deduplication

            AddIfNotEmpty(tags, tender.GetSourceType()); // Fallback tag 1: Source Type
            AddIfNotEmpty(tags, tender.Province); // Fallback tag 2: Province 

            // Add source-specific fields
            switch (tender)
            {
                // eTender Fallback Tags
                case ETenderMessage e:
                    AddIfNotEmpty(tags, e.Audience); // Audience is often the Department in eTenders
                    break;

                // Transnet Fallback Tags
                case TransnetTenderMessage t:
                    AddIfNotEmpty(tags, t.Institution);
                    AddIfNotEmpty(tags, t.Category);
                    AddIfNotEmpty(tags, t.Location);
                    break;

                // SANRAL Fallback Tags
                case SanralTenderMessage sn:
                    AddIfNotEmpty(tags, sn.Category);
                    AddIfNotEmpty(tags, sn.Region);
                    break;

                // SARS Fallback Tags
                case SarsTenderMessage sars:
                    // BriefingSession is a good tag
                    if (!string.IsNullOrWhiteSpace(sars.BriefingSession))
                        tags.Add("Briefing Session");
                    break;
            }
            return tags.ToList();
        }

        /// <summary>
        /// Helper to add a non-empty string to a HashSet.
        /// </summary>
        private void AddIfNotEmpty(HashSet<string> set, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value.Trim());
            }
        }

        /// <summary>
        /// Now returns a clean List<string> from the provided raw tags
        /// Applies the quality gatekeeper rules: blocklist, mapping, casing, de-pluralization, uniqueness.
        /// </summary>
        private List<string> ApplyQualityGatekeeper(IEnumerable<string> rawTags, HashSet<string> blocklist, Dictionary<string, string> tagMap)
        {
            var cleanTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var textInfo = CultureInfo.InvariantCulture.TextInfo;

            foreach (var rawTag in rawTags)
            {
                if (string.IsNullOrWhiteSpace(rawTag)) continue;

                string currentTag = rawTag.Trim('"', ' ', '.', '\n', '\r'); // Extra cleaning
                string lowerTag = currentTag.ToLowerInvariant();

                // 1. Check length and blocklist
                if (currentTag.Length <= 2 || blocklist.Contains(lowerTag))
                {
                    _logger.LogTrace("Tag blocked or too short: '{RawTag}'", rawTag);
                    continue;
                }

                // 2. Check map
                if (tagMap.TryGetValue(lowerTag, out var mappedTag))
                {
                    currentTag = mappedTag; // Use the canonical version from the map
                    _logger.LogTrace("Tag mapped: '{RawTag}' -> '{MappedTag}'", rawTag, currentTag);
                }
                else
                {
                    // 3. Apply Title Case if not mapped
                    currentTag = textInfo.ToTitleCase(currentTag);
                }

                // 4. Simple De-pluralization for Uniqueness Check
                string singularCheck = currentTag.EndsWith("s") ? currentTag[..^1] : currentTag;

                // Check if a singular or plural version is already in the set
                if (cleanTags.Contains(singularCheck) || cleanTags.Contains(singularCheck + "s"))
                {
                    _logger.LogTrace("Tag considered duplicate (singular/plural form exists): '{CurrentTag}'", currentTag);
                    continue;
                }

                cleanTags.Add(currentTag); // Add the processed (mapped or title-cased) tag
            }

            return cleanTags.ToList();
        }

        // Helper methods adapted from the BedrockSummaryService 
        private int CalculateBackoffDelay(int attempt)
        {
            // Exponential backoff: ~1.5s, 3s, 6s, 12s, 24s (for 6 attempts)
            var exponentialDelay = BaseDelayMs * Math.Pow(2, attempt - 1);
            var random = new Random();
            var jitter = random.NextDouble() * 0.5 + 0.75; // 0.75 to 1.25

            // Cap at 30 seconds maximum to prevent excessive delays
            return Math.Min((int)(exponentialDelay * jitter), 30000);
        }

        private string ParseClaudeResponse(string responseJson) // Reused from Summarization
        {
            try
            {
                using var document = JsonDocument.Parse(responseJson);
                var contentArray = document.RootElement.GetProperty("content");

                if (contentArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var contentItem in contentArray.EnumerateArray())
                    {
                        if (contentItem.TryGetProperty("type", out var typeProperty) && typeProperty.GetString() == "text" &&
                            contentItem.TryGetProperty("text", out var textProperty))
                        {
                            return textProperty.GetString() ?? string.Empty; // Return empty if text is null
                        }
                    }
                }
                _logger.LogWarning("Unexpected Claude 3 response format: 'text' field not found.");
                return string.Empty; // Return empty on unexpected format
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Claude 3 response JSON: {ResponseJson}", responseJson);
                return string.Empty; // Return empty on exception
            }
        }
    }
}
