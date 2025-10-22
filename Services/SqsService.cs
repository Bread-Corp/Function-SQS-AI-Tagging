using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Tender_AI_Tagging_Lambda.Interfaces;
using System.Text;
using System.Text.Json;

namespace Tender_AI_Tagging_Lambda.Services
{
    /// <summary>
    /// Production-ready SQS service implementation for tender message processing
    /// </summary>
    public class SqsService : ISqsService
    {
        private readonly IAmazonSQS _sqsClient;
        private readonly ILogger<SqsService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public SqsService(IAmazonSQS sqsClient, ILogger<SqsService> logger)
        {
            _sqsClient = sqsClient ?? throw new ArgumentNullException(nameof(sqsClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Configure JSON serialization options for consistent message formatting
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false  // Compact JSON for efficiency
            };
        }

        public async Task SendMessageAsync(string queueUrl, object message)
        {
            // Validate input parameters
            if (string.IsNullOrWhiteSpace(queueUrl))
                throw new ArgumentException("Queue URL cannot be null or empty", nameof(queueUrl));
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            var messageType = message.GetType().Name;
            _logger.LogDebug("Starting to send {MessageType} message to queue: {QueueUrl}", messageType, queueUrl);

            try
            {
                // Serialize the message object to JSON
                var messageBody = JsonSerializer.Serialize(message, _jsonOptions);
                var bodySize = Encoding.UTF8.GetByteCount(messageBody);

                _logger.LogDebug("Serialized message body size: {BodySize} bytes", bodySize);

                // Create the basic send message request
                var request = new SendMessageRequest
                {
                    QueueUrl = queueUrl,
                    MessageBody = messageBody
                };

                // Handle FIFO queue specific requirements
                if (queueUrl.EndsWith(".fifo"))
                {
                    // FIFO queues require MessageGroupId and MessageDeduplicationId
                    var messageGroupId = GetMessageGroupId(message, messageBody);
                    var deduplicationId = Guid.NewGuid().ToString();

                    request.MessageGroupId = messageGroupId;
                    request.MessageDeduplicationId = deduplicationId;

                    _logger.LogInformation("Sending {MessageType} to FIFO queue {QueueUrl} with GroupId: {MessageGroupId}, DeduplicationId: {DeduplicationId}",
                        messageType, queueUrl, messageGroupId, deduplicationId);
                }
                else
                {
                    _logger.LogInformation("Sending {MessageType} to standard queue {QueueUrl}", messageType, queueUrl);
                }

                // Send the message to SQS
                var response = await _sqsClient.SendMessageAsync(request);

                _logger.LogInformation("Message sent successfully. QueueUrl: {QueueUrl}, MessageId: {MessageId}, MessageType: {MessageType}",
                    queueUrl, response.MessageId, messageType);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSON serialization failed for {MessageType} to queue {QueueUrl}", messageType, queueUrl);
                throw new InvalidOperationException($"Failed to serialize message of type {messageType}", jsonEx);
            }
            catch (AmazonSQSException sqsEx)
            {
                _logger.LogError(sqsEx, "SQS operation failed for {MessageType} to queue {QueueUrl}. ErrorCode: {ErrorCode}",
                    messageType, queueUrl, sqsEx.ErrorCode);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error sending {MessageType} to queue {QueueUrl}", messageType, queueUrl);
                throw;
            }
        }

        public async Task SendMessageBatchAsync(string queueUrl, List<object> messages)
        {
            // Validate input parameters
            if (string.IsNullOrWhiteSpace(queueUrl))
                throw new ArgumentException("Queue URL cannot be null or empty", nameof(queueUrl));
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));

            // Early return if no messages to send
            if (!messages.Any())
            {
                _logger.LogDebug("No messages provided for batch send to {QueueUrl}", queueUrl);
                return;
            }

            var totalMessages = messages.Count;
            var isFifoQueue = queueUrl.EndsWith(".fifo");

            _logger.LogInformation("Starting batch send of {MessageCount} messages to {QueueType} queue: {QueueUrl}",
                totalMessages, isFifoQueue ? "FIFO" : "standard", queueUrl);

            try
            {
                // AWS SQS batch limit is 10 messages per request
                const int batchSize = 10;
                var totalBatches = (int)Math.Ceiling((double)totalMessages / batchSize);
                var successfulMessages = 0;
                var failedMessages = 0;

                // Process messages in batches of 10
                for (int batchIndex = 0; batchIndex < totalBatches; batchIndex++)
                {
                    var startIndex = batchIndex * batchSize;
                    var batch = messages.Skip(startIndex).Take(batchSize).ToList();

                    _logger.LogDebug("Processing batch {BatchNumber}/{TotalBatches} with {BatchSize} messages",
                        batchIndex + 1, totalBatches, batch.Count);

                    // Prepare batch entries for SQS
                    var entries = batch.Select((message, index) =>
                    {
                        var messageBody = JsonSerializer.Serialize(message, _jsonOptions);
                        var entryId = $"msg_{startIndex + index}";

                        var entry = new SendMessageBatchRequestEntry
                        {
                            Id = entryId,
                            MessageBody = messageBody
                        };

                        // Handle FIFO queue requirements
                        if (isFifoQueue)
                        {
                            var messageGroupId = GetMessageGroupId(message, messageBody);
                            var deduplicationId = Guid.NewGuid().ToString();

                            entry.MessageGroupId = messageGroupId;
                            entry.MessageDeduplicationId = deduplicationId;

                            _logger.LogTrace("FIFO entry {EntryId} configured with GroupId: {MessageGroupId}",
                                entryId, messageGroupId);
                        }

                        return entry;
                    }).ToList();

                    // Create and send the batch request
                    var request = new SendMessageBatchRequest
                    {
                        QueueUrl = queueUrl,
                        Entries = entries
                    };

                    var response = await _sqsClient.SendMessageBatchAsync(request);

                    // Validate response (defensive programming)
                    if (response == null)
                    {
                        var errorMsg = "SQS SendMessageBatchAsync returned null response";
                        _logger.LogError(errorMsg + " for queue {QueueUrl}, batch {BatchNumber}", queueUrl, batchIndex + 1);
                        throw new InvalidOperationException(errorMsg);
                    }

                    // Process batch results
                    var batchSuccessCount = response.Successful?.Count ?? 0;
                    var batchFailedCount = response.Failed?.Count ?? 0;

                    successfulMessages += batchSuccessCount;
                    failedMessages += batchFailedCount;

                    _logger.LogInformation("Batch {BatchNumber}/{TotalBatches} completed. Successful: {SuccessCount}, Failed: {FailCount}",
                        batchIndex + 1, totalBatches, batchSuccessCount, batchFailedCount);

                    // Log details of any failed messages
                    if (response.Failed != null && response.Failed.Any())
                    {
                        foreach (var failed in response.Failed)
                        {
                            _logger.LogWarning("Batch message failed - Id: {MessageId}, ErrorCode: {ErrorCode}, ErrorMessage: {ErrorMessage}",
                                failed.Id, failed.Code, failed.Message);
                        }

                        // Throw exception for failed messages to ensure they are not lost
                        throw new InvalidOperationException($"Failed to send {batchFailedCount} messages in batch {batchIndex + 1}");
                    }
                }

                _logger.LogInformation("Batch send completed successfully. Total messages sent: {SuccessfulCount}/{TotalCount} to queue {QueueUrl}",
                    successfulMessages, totalMessages, queueUrl);
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "JSON serialization failed during batch send to queue {QueueUrl}", queueUrl);
                throw new InvalidOperationException("Failed to serialize one or more messages in batch", jsonEx);
            }
            catch (AmazonSQSException sqsEx)
            {
                _logger.LogError(sqsEx, "SQS batch operation failed for queue {QueueUrl}. ErrorCode: {ErrorCode}", queueUrl, sqsEx.ErrorCode);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during batch send to queue {QueueUrl}", queueUrl);
                throw;
            }
        }

        public async Task DeleteMessageAsync(string queueUrl, string receiptHandle)
        {
            // Validate input parameters
            if (string.IsNullOrWhiteSpace(queueUrl))
                throw new ArgumentException("Queue URL cannot be null or empty", nameof(queueUrl));
            if (string.IsNullOrWhiteSpace(receiptHandle))
                throw new ArgumentException("Receipt handle cannot be null or empty", nameof(receiptHandle));

            _logger.LogDebug("Deleting message from queue {QueueUrl} with receipt handle: {ReceiptHandle}", queueUrl, receiptHandle);

            try
            {
                var request = new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = receiptHandle
                };

                await _sqsClient.DeleteMessageAsync(request);

                _logger.LogInformation("Message deleted successfully from queue {QueueUrl}", queueUrl);
            }
            catch (AmazonSQSException sqsEx)
            {
                _logger.LogError(sqsEx, "SQS delete operation failed for queue {QueueUrl}. ErrorCode: {ErrorCode}, ReceiptHandle: {ReceiptHandle}",
                    queueUrl, sqsEx.ErrorCode, receiptHandle);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error deleting message from queue {QueueUrl}", queueUrl);
                throw;
            }
        }

        public async Task DeleteMessageBatchAsync(string queueUrl, List<(string id, string receiptHandle)> messages)
        {
            // Validate input parameters
            if (string.IsNullOrWhiteSpace(queueUrl))
                throw new ArgumentException("Queue URL cannot be null or empty", nameof(queueUrl));
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));

            // Early return if no messages to delete
            if (!messages.Any())
            {
                _logger.LogDebug("No messages provided for batch delete from {QueueUrl}", queueUrl);
                return;
            }

            var totalMessages = messages.Count;
            _logger.LogInformation("Starting batch delete of {MessageCount} messages from queue {QueueUrl}", totalMessages, queueUrl);

            try
            {
                // AWS SQS batch limit is 10 messages per request
                const int batchSize = 10;
                var totalBatches = (int)Math.Ceiling((double)totalMessages / batchSize);
                var successfulDeletes = 0;
                var failedDeletes = 0;

                for (int batchIndex = 0; batchIndex < totalBatches; batchIndex++)
                {
                    var startIndex = batchIndex * batchSize;
                    var batch = messages.Skip(startIndex).Take(batchSize).ToList();

                    _logger.LogDebug("Processing delete batch {BatchNumber}/{TotalBatches} with {BatchSize} messages",
                        batchIndex + 1, totalBatches, batch.Count);

                    // Create delete batch entries
                    var entries = batch.Select(m => new DeleteMessageBatchRequestEntry
                    {
                        Id = m.id,
                        ReceiptHandle = m.receiptHandle
                    }).ToList();

                    var request = new DeleteMessageBatchRequest
                    {
                        QueueUrl = queueUrl,
                        Entries = entries
                    };

                    var response = await _sqsClient.DeleteMessageBatchAsync(request);

                    // Validate response (defensive programming)
                    if (response == null)
                    {
                        var errorMsg = "SQS DeleteMessageBatchAsync returned null response";
                        _logger.LogError(errorMsg + " for queue {QueueUrl}, batch {BatchNumber}", queueUrl, batchIndex + 1);
                        throw new InvalidOperationException(errorMsg);
                    }

                    // Process batch delete results
                    var batchSuccessCount = response.Successful?.Count ?? 0;
                    var batchFailedCount = response.Failed?.Count ?? 0;

                    successfulDeletes += batchSuccessCount;
                    failedDeletes += batchFailedCount;

                    _logger.LogInformation("Delete batch {BatchNumber}/{TotalBatches} completed. Successful: {SuccessCount}, Failed: {FailCount}",
                        batchIndex + 1, totalBatches, batchSuccessCount, batchFailedCount);

                    // Log details of any failed deletions (but don't throw - partial success is acceptable for deletes)
                    if (response.Failed != null && response.Failed.Any())
                    {
                        foreach (var failed in response.Failed)
                        {
                            _logger.LogWarning("Failed to delete message - Id: {MessageId}, ErrorCode: {ErrorCode}, ErrorMessage: {ErrorMessage}",
                                failed.Id, failed.Code, failed.Message);
                        }
                    }
                }

                _logger.LogInformation("Batch delete completed. Successfully deleted: {SuccessfulCount}/{TotalCount} messages from queue {QueueUrl}",
                    successfulDeletes, totalMessages, queueUrl);
            }
            catch (AmazonSQSException sqsEx)
            {
                _logger.LogError(sqsEx, "SQS batch delete operation failed for queue {QueueUrl}. ErrorCode: {ErrorCode}", queueUrl, sqsEx.ErrorCode);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during batch delete from queue {QueueUrl}", queueUrl);
                throw;
            }
        }

        /// <summary>
        /// Extracts or generates MessageGroupId for FIFO queues using multiple strategies
        /// </summary>
        private string GetMessageGroupId(object message, string messageBody)
        {
            var messageTypeName = message?.GetType().Name ?? "Unknown";

            try
            {
                // Early validation
                if (message == null)
                {
                    _logger.LogWarning("Message object is null, using fallback MessageGroupId");
                    return "NullMessage";
                }

                if (string.IsNullOrEmpty(messageBody))
                {
                    _logger.LogWarning("Message body is empty for {MessageType}, using fallback MessageGroupId", messageTypeName);
                    return "EmptyMessage";
                }

                _logger.LogTrace("Extracting MessageGroupId from {MessageType}", messageTypeName);

                // Strategy 1: Use GetSourceType method if available (preferred for tender messages)
                var getSourceTypeMethod = message.GetType().GetMethod("GetSourceType");
                if (getSourceTypeMethod != null)
                {
                    var sourceType = getSourceTypeMethod.Invoke(message, null)?.ToString();
                    if (!string.IsNullOrEmpty(sourceType))
                    {
                        var sanitized = SanitizeMessageGroupId(sourceType);
                        _logger.LogTrace("Using GetSourceType() result as MessageGroupId: {MessageGroupId}", sanitized);
                        return sanitized;
                    }
                }

                // Strategy 2: Check for MessageGroupId property on the object
                var messageGroupIdProperty = message.GetType().GetProperty("MessageGroupId");
                if (messageGroupIdProperty != null)
                {
                    var messageGroupId = messageGroupIdProperty.GetValue(message)?.ToString();
                    if (!string.IsNullOrEmpty(messageGroupId))
                    {
                        var sanitized = SanitizeMessageGroupId(messageGroupId);
                        _logger.LogTrace("Using MessageGroupId property: {MessageGroupId}", sanitized);
                        return sanitized;
                    }
                }

                // Strategy 3: Parse JSON to find MessageGroupId fields
                using var document = JsonDocument.Parse(messageBody);
                var root = document.RootElement;

                // Check standard MessageGroupId field variations
                var messageGroupIdFields = new[] { "MessageGroupId", "messageGroupId" };
                foreach (var field in messageGroupIdFields)
                {
                    if (root.TryGetProperty(field, out var property) && property.ValueKind == JsonValueKind.String)
                    {
                        var value = property.GetString();
                        if (!string.IsNullOrEmpty(value))
                        {
                            var sanitized = SanitizeMessageGroupId(value);
                            _logger.LogTrace("Using JSON field '{Field}' as MessageGroupId: {MessageGroupId}", field, sanitized);
                            return sanitized;
                        }
                    }
                }

                // Strategy 4: Use Organization field for tender-specific grouping
                if (root.TryGetProperty("Organization", out var orgProperty) && orgProperty.ValueKind == JsonValueKind.String)
                {
                    var organization = orgProperty.GetString();
                    if (!string.IsNullOrEmpty(organization))
                    {
                        var sanitized = SanitizeMessageGroupId(organization);
                        _logger.LogTrace("Using Organization field as MessageGroupId: {MessageGroupId}", sanitized);
                        return sanitized;
                    }
                }

                // Strategy 5: Handle anonymous types (typically system-generated objects)
                if (messageTypeName.Contains("AnonymousType") || messageTypeName.Contains("<>"))
                {
                    _logger.LogTrace("Anonymous type detected, using 'SystemGenerated' MessageGroupId");
                    return "SystemGenerated";
                }

                // Final fallback based on message type
                var fallbackGroupId = $"Type_{messageTypeName}";
                var sanitizedFallback = SanitizeMessageGroupId(fallbackGroupId);

                _logger.LogDebug("No specific MessageGroupId found for {MessageType}, using type-based fallback: {MessageGroupId}",
                    messageTypeName, sanitizedFallback);

                return sanitizedFallback;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogWarning(jsonEx, "JSON parsing error while extracting MessageGroupId from {MessageType}, using fallback", messageTypeName);
                return SanitizeMessageGroupId($"ParseError_{messageTypeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error extracting MessageGroupId from {MessageType}, using default fallback", messageTypeName);
                return "DefaultGroup";
            }
        }

        /// <summary>
        /// Sanitizes MessageGroupId to comply with AWS SQS requirements
        /// </summary>
        private string SanitizeMessageGroupId(string input)
        {
            // Handle null or empty input
            if (string.IsNullOrEmpty(input))
                return "DefaultGroup";

            // Remove invalid characters - keep only alphanumeric, hyphens, and underscores
            var sanitized = System.Text.RegularExpressions.Regex.Replace(input, @"[^a-zA-Z0-9\-_]", "");

            // Ensure result is not empty after sanitization
            if (string.IsNullOrEmpty(sanitized))
                return "DefaultGroup";

            // Enforce AWS SQS 128 character limit
            if (sanitized.Length > 128)
                sanitized = sanitized.Substring(0, 128);

            return sanitized;
        }
    }
}
