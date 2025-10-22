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
        private const int MaxRetryAttempts = 4; // Adjusted retry attempts for tagging
        private const int BaseDelayMs = 500;   // Adjusted base delay

        // Concurrency control for Bedrock
        private static readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1); // Max 1 concurrent request per Lambda

        public TaggingService(IAmazonBedrockRuntime bedrockClient, IConfigService configService, ILogger<TaggingService> logger)
        {
            _bedrockClient = bedrockClient ?? throw new ArgumentNullException(nameof(bedrockClient));
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        public async Task<List<string>> GenerateAndCleanTagsAsync(TenderMessageBase tenderMessage)
        {
            // Step 1: Clear Existing Tags
            tenderMessage.Tags.Clear(); // Start fresh
            _logger.LogDebug("Cleared existing tags for TenderNumber: {TenderNumber}", tenderMessage.TenderNumber);

            // Step 2: Get Configuration
            var blocklist = await _configService.GetTagBlocklistAsync();
            var tagMap = await _configService.GetTagMapAsync();
            var combinedPrompt = await _configService.GetCombinedTaggingPromptAsync(tenderMessage.GetSourceType());

            // Step 3: Extract Fallback Tags First (Resilience step)
            var fallbackTags = ExtractFallbackTags(tenderMessage);
            _logger.LogDebug("Extracted {Count} fallback tags for TenderNumber: {TenderNumber}", fallbackTags.Count, tenderMessage.TenderNumber);

            // Step 4: Generate AI Tags via Bedrock
            List<string> bedrockTags = new List<string>();
            try
            {
                string inputText = PrepareInputText(tenderMessage);
                if (!string.IsNullOrWhiteSpace(inputText))
                {
                    // WRAP THE BEDROCK CALL IN THE SEMAPHORE
                    await _rateLimitSemaphore.WaitAsync(); // Wait for our turn

                    try
                    {
                        string rawTagString = await ExecuteBedrockRequestWithRetryAsync(combinedPrompt, inputText, tenderMessage.TenderNumber ?? "Unknown");
                        bedrockTags = ParseBedrockTagResponse(rawTagString);
                        _logger.LogDebug("Bedrock generated {Count} raw tags for TenderNumber: {TenderNumber}", bedrockTags.Count, tenderMessage.TenderNumber);
                    }
                    finally
                    {
                        _rateLimitSemaphore.Release(); // Always release the semaphore
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
                // Continue execution with just fallback tags
            }

            // Step 5: Combine and Clean Tags
            var combinedRawTags = fallbackTags.Concat(bedrockTags);
            var finalTags = ApplyQualityGatekeeper(combinedRawTags, blocklist, tagMap);

            // Step 6: Sort and Return
            finalTags.Sort();
            _logger.LogInformation("Generated {Count} final tags for TenderNumber: {TenderNumber}", finalTags.Count, tenderMessage.TenderNumber);
            return finalTags;
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
        /// </summary>
        private async Task<string> ExecuteBedrockRequestWithRetryAsync(string combinedPrompt, string inputText, string tenderNumber)
        {
            var attempt = 0;
            while (attempt < MaxRetryAttempts)
            {
                attempt++;
                try
                {
                    _logger.LogDebug("Bedrock tagging request attempt {Attempt}/{MaxAttempts} - TenderNumber: {TenderNumber}", attempt, MaxRetryAttempts, tenderNumber);

                    var payload = new
                    {
                        anthropic_version = "bedrock-2023-05-31",
                        max_tokens = 300, // Tagging requires fewer tokens than summarization
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
                                        // Instruct clearly, provide context, ask for comma-separated output
                                        text = $"{combinedPrompt}\n\nAnalyze the following tender text and extract relevant keywords and tags. Focus on the core subject matter, required services/goods, industry, and location. Return the tags as a simple comma-separated list.\n\nTender Text:\n{inputText}"
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

            AddIfNotEmpty(tags, tender.GetSourceType());
            AddIfNotEmpty(tags, tender.Province);

            // Add source-specific fields
            switch (tender)
            {
                case ETenderMessage e: AddIfNotEmpty(tags, e.Status); break;
                case TransnetTenderMessage t: AddIfNotEmpty(tags, t.Institution); AddIfNotEmpty(tags, t.Category); AddIfNotEmpty(tags, t.Location); break;
                case SanralTenderMessage sn: AddIfNotEmpty(tags, sn.Category); AddIfNotEmpty(tags, sn.Region); break;
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
        /// Applies the quality gatekeeper rules: blocklist, mapping, casing, de-pluralization, uniqueness.
        /// </summary>
        private List<string> ApplyQualityGatekeeper(IEnumerable<string> rawTags, HashSet<string> blocklist, Dictionary<string, string> tagMap)
        {
            var finalTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textInfo = CultureInfo.InvariantCulture.TextInfo;

            foreach (var rawTag in rawTags)
            {
                if (string.IsNullOrWhiteSpace(rawTag)) continue;

                string currentTag = rawTag.Trim();
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

                // 4. Simple De-pluralization for Uniqueness Check (Add the original form if unique)
                // We check the singular form for existence, but add the potentially plural form.
                string singularCheck = currentTag.EndsWith('s') ? currentTag.Substring(0, currentTag.Length - 1) : currentTag;

                // Check uniqueness based on singular form (case-insensitive)
                bool alreadyExists = false;
                foreach (var existingTag in finalTags)
                {
                    string existingSingular = existingTag.EndsWith('s') ? existingTag.Substring(0, existingTag.Length - 1) : existingTag;
                    if (string.Equals(singularCheck, existingSingular, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyExists = true;
                        _logger.LogTrace("Tag considered duplicate (singular form exists): '{CurrentTag}' (singular: '{SingularCheck}')", currentTag, singularCheck);
                        break;
                    }
                }

                if (!alreadyExists)
                {
                    finalTags.Add(currentTag); // Add the processed (mapped or title-cased) tag
                }
            }

            return finalTags.ToList(); // Convert back to list for sorting later
        }

        // Helper methods adapted from the BedrockSummaryService 
        private int CalculateBackoffDelay(int attempt)
        {
            var exponentialDelay = BaseDelayMs * Math.Pow(2, attempt - 1);
            var random = new Random();
            var jitter = random.NextDouble() * 0.5 + 0.75; // 0.75 to 1.25
            return Math.Min((int)(exponentialDelay * jitter), 15000); // Shorter max delay for tagging
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
