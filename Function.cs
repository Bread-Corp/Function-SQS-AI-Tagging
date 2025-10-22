using Amazon.BedrockRuntime;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SimpleSystemsManagement;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tender_AI_Tagging_Lambda.Interfaces; 
using Tender_AI_Tagging_Lambda.Models;     
using Tender_AI_Tagging_Lambda.Services;   


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Tender_AI_Tagging_Lambda;

/// <summary>
/// AWS Lambda function to process tender messages from SQS, generate relevant tags using Bedrock,
/// apply quality rules, and route messages to the appropriate queues.
/// </summary>
public class Function
{
    private readonly ISqsService _sqsService;
    private readonly IMessageFactory _messageFactory;
    private readonly ITaggingService _taggingService;
    private readonly ILogger<Function> _logger;
    private readonly IAmazonSQS _sqsClient;

    // Queue URLs from environment variables
    private readonly string _sourceQueueUrl; // TagQueue.fifo
    private readonly string _writeQueueUrl;  // WriteQueue.fifo
    private readonly string _failedQueueUrl; // TagFailedQueue.fifo

    // JSON options for consistent serialization
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull // Exclude null properties
    };

    /// <summary>
    /// Default constructor for Lambda runtime with DI setup.
    /// </summary>
    public Function() : this(null, null, null, null, null) { }

    /// <summary>
    /// Constructor with DI support for testing.
    /// </summary>
    public Function(ISqsService? sqsService, IMessageFactory? messageFactory, ITaggingService? taggingService, ILogger<Function>? logger, IAmazonSQS? sqsClient)
    {
        var serviceProvider = ConfigureServices();

        _sqsService = sqsService ?? serviceProvider.GetRequiredService<ISqsService>();
        _messageFactory = messageFactory ?? serviceProvider.GetRequiredService<IMessageFactory>();
        _taggingService = taggingService ?? serviceProvider.GetRequiredService<ITaggingService>();
        _logger = logger ?? serviceProvider.GetRequiredService<ILogger<Function>>();
        _sqsClient = sqsClient ?? serviceProvider.GetRequiredService<IAmazonSQS>();

        // Load and validate environment variables
        _sourceQueueUrl = Environment.GetEnvironmentVariable("SOURCE_QUEUE_URL") ?? throw new InvalidOperationException("SOURCE_QUEUE_URL is required.");
        _writeQueueUrl = Environment.GetEnvironmentVariable("WRITE_QUEUE_URL") ?? throw new InvalidOperationException("WRITE_QUEUE_URL is required.");
        _failedQueueUrl = Environment.GetEnvironmentVariable("FAILED_QUEUE_URL") ?? throw new InvalidOperationException("FAILED_QUEUE_URL is required.");

        _logger.LogInformation("AI Tagging Lambda initialized. Source: {Source}, Write: {Write}, Failed: {Failed}",
            !string.IsNullOrEmpty(_sourceQueueUrl), !string.IsNullOrEmpty(_writeQueueUrl), !string.IsNullOrEmpty(_failedQueueUrl));
    }

    /// <summary>
    /// Configures the dependency injection container.
    /// </summary>
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddJsonConsole(options => // Use JSON console logger for structured CloudWatch logs
            {
                options.IncludeScopes = false;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fffZ";
                options.JsonWriterOptions = new JsonWriterOptions { Indented = false }; // Compact logs
            });
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
        });

        // Register AWS Service Clients (Singleton is generally recommended for AWS SDK clients)
        services.AddSingleton<IAmazonSQS, AmazonSQSClient>();
        services.AddSingleton<IAmazonBedrockRuntime, AmazonBedrockRuntimeClient>();
        services.AddSingleton<IAmazonSimpleSystemsManagement, AmazonSimpleSystemsManagementClient>();

        // Register Application Services
        services.AddTransient<ISqsService, SqsService>();
        services.AddTransient<IMessageFactory, MessageFactory>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddTransient<ITaggingService, TaggingService>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Main Lambda handler with continuous polling.
    /// </summary>
    public async Task<string> FunctionHandler(SQSEvent evnt, ILambdaContext context)
    {
        var functionStart = DateTime.UtcNow;
        var totalProcessed = 0;
        var totalFailed = 0;
        var totalDeleted = 0;
        var batchCount = 0;

        _logger.LogInformation("Lambda invocation started - RequestId: {RequestId}, InitialEventMessages: {InitialMessageCount}",
            context.AwsRequestId, evnt.Records?.Count ?? 0);

        try
        {
            // Process initial batch from trigger
            if (evnt?.Records?.Any() == true)
            {
                batchCount++;
                _logger.LogInformation("Processing initial SQS event batch #{BatchNumber} - MessageCount: {MessageCount}", batchCount, evnt.Records.Count);
                var initialMessages = evnt.Records.Select(ConvertSqsEventToMessage).ToList();
                var initialResult = await ProcessMessageBatch(initialMessages, batchCount);
                totalProcessed += initialResult.processed;
                totalFailed += initialResult.failed;
                totalDeleted += initialResult.deleted;
            }

            // Continuous polling loop
            while (context.RemainingTime > TimeSpan.FromSeconds(30)) // Safety margin
            {
                var messages = await PollMessagesFromQueue(10); // Max SQS batch size
                if (!messages.Any())
                {
                    _logger.LogInformation("Queue polling complete. No more messages found.");
                    break;
                }

                batchCount++;
                var batchResult = await ProcessMessageBatch(messages, batchCount);
                totalProcessed += batchResult.processed;
                totalFailed += batchResult.failed;
                totalDeleted += batchResult.deleted;

                await Task.Delay(100); // Small delay
            }

            var totalDuration = (DateTime.UtcNow - functionStart).TotalMilliseconds;
            var result = $"Success. Batches: {batchCount}, Processed: {totalProcessed}, Failed: {totalFailed}, Deleted: {totalDeleted}, Duration: {totalDuration:F0}ms";
            _logger.LogInformation("Lambda execution finished. {Result}", result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lambda execution failed unexpectedly. Processed: {TotalProcessed}, Failed: {TotalFailed}", totalProcessed, totalFailed);
            throw; // Re-throw to indicate failure to Lambda runtime
        }
    }

    /// <summary>
    /// Polls messages directly from the source queue.
    /// </summary>
    private async Task<List<QueueMessage>> PollMessagesFromQueue(int maxMessages)
    {
        try
        {
            var request = new ReceiveMessageRequest
            {
                QueueUrl = _sourceQueueUrl,
                MaxNumberOfMessages = maxMessages,
                WaitTimeSeconds = 2,
                VisibilityTimeout = 300, // 5 minutes processing time
                MessageSystemAttributeNames = new List<string> { "All" },
                MessageAttributeNames = new List<string> { "All" }
            };
            var response = await _sqsClient.ReceiveMessageAsync(request);
            _logger.LogDebug("Polled {MessageCount} messages from source queue.", response.Messages?.Count ?? 0);
            return response.Messages?.Select(ConvertSqsApiMessageToQueueMessage).ToList() ?? new List<QueueMessage>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to poll messages from source queue: {QueueUrl}", _sourceQueueUrl);
            return new List<QueueMessage>(); // Return empty list on error to allow graceful exit
        }
    }

    /// <summary>
    /// Processes a batch of SQS messages: deserialize, tag, route, delete.
    /// </summary>
    private async Task<(int processed, int failed, int deleted)> ProcessMessageBatch(List<QueueMessage> messages, int batchNumber)
    {
        var batchStart = DateTime.UtcNow;
        var successMessages = new List<(TenderMessageBase message, QueueMessage record)>();
        var failedMessages = new List<(string originalBody, string messageGroupId, Exception exception, QueueMessage record)>();

        _logger.LogInformation(
            "Starting Batch Processing. BatchNumber: {BatchNumber}, TenderSource: {TenderSource}, MessageCount: {MessageCount}",
            batchNumber,
            messages.FirstOrDefault()?.MessageGroupId ?? "Unknown", // Representative source
            messages.Count
        );

        foreach (var message in messages)
        {
            TenderMessageBase? tenderMessage = null;
            try
            {
                if (string.IsNullOrEmpty(message.Body)) throw new InvalidOperationException("Message body is null or empty.");

                tenderMessage = _messageFactory.CreateMessage(message.Body, message.MessageGroupId);
                if (tenderMessage == null) throw new InvalidOperationException($"Message factory returned null for GroupId: {message.MessageGroupId}");

                // Generate and assign the new tags
                List<string> newTags = await _taggingService.GenerateAndCleanTagsAsync(tenderMessage);
                tenderMessage.Tags = newTags; // Replace existing tags

                successMessages.Add((tenderMessage, message));
                _logger.LogDebug("Successfully processed message {MessageId} for tender {TenderNumber}", message.MessageId, tenderMessage.TenderNumber);

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Message processing failed - MessageId: {MessageId}, GroupId: {MessageGroupId}", message.MessageId, message.MessageGroupId);
                // Capture original body for DLQ
                failedMessages.Add((message.Body, message.MessageGroupId, ex, message));
            }
        }

        // --- Phase 2: Routing ---
        var successfullySentIds = new HashSet<string>();

        // Send successful messages to Write Queue
        if (successMessages.Any())
        {
            try
            {
                var messagesToSend = successMessages.Select(sm => sm.message).Cast<object>().ToList();
                await _sqsService.SendMessageBatchAsync(_writeQueueUrl, messagesToSend);
                successfullySentIds.UnionWith(successMessages.Select(sm => sm.record.MessageId));
                _logger.LogInformation("Successfully sent {Count} processed messages to WriteQueue.", successMessages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send batch to WriteQueue. These messages will be retried or need manual intervention from DLQ later.");
                // Move messages that failed to send to the failed list
                foreach (var (msg, record) in successMessages)
                {
                    failedMessages.Add((JsonSerializer.Serialize(msg, _jsonOptions), record.MessageGroupId, ex, record));
                }
            }
        }

        // Send failed messages to Failed Queue (DLQ)
        if (failedMessages.Any())
        {
            try
            {
                var dlqMessages = failedMessages.Select(f => new // Create enriched error payload
                {
                    OriginalMessageBody = f.originalBody,
                    MessageGroupId = f.messageGroupId,
                    ErrorMessage = f.exception.Message,
                    ErrorType = f.exception.GetType().Name,
                    StackTrace = f.exception.StackTrace, // Include stack trace for debugging
                    ProcessedBy = "Sqs_Tagging_Lambda",
                    Timestamp = DateTime.UtcNow
                }).Cast<object>().ToList();

                await _sqsService.SendMessageBatchAsync(_failedQueueUrl, dlqMessages);
                // Add failed message IDs to the set that should be deleted from source
                successfullySentIds.UnionWith(failedMessages.Select(fm => fm.record.MessageId));
                _logger.LogInformation("Successfully sent {Count} failed messages to TagFailedQueue.", failedMessages.Count);
            }
            catch (Exception dlqEx)
            {
                _logger.LogCritical(dlqEx, "CRITICAL: Failed to send {Count} messages to TagFailedQueue. Potential data loss. Messages will be retried by SQS.", failedMessages.Count);
                // Do NOT add to successfullySentIds here, SQS will retry them
                throw; // Re-throw critical DLQ failure
            }
        }

        // --- Phase 3: Deletion ---
        var messagesToDelete = messages
            .Where(m => successfullySentIds.Contains(m.MessageId))
            .Select(m => (m.MessageId, m.ReceiptHandle))
            .ToList();

        var deletedCount = 0;
        if (messagesToDelete.Any())
        {
            try
            {
                await _sqsService.DeleteMessageBatchAsync(_sourceQueueUrl, messagesToDelete);
                deletedCount = messagesToDelete.Count;
                _logger.LogInformation("Successfully deleted {Count} messages from source queue (TagQueue).", deletedCount);
            }
            catch (Exception deleteEx)
            {
                // Log delete failures but don't fail the batch - SQS will make them visible again.
                _logger.LogError(deleteEx, "Failed to delete {Count} messages from source queue (TagQueue). They will likely be reprocessed.", messagesToDelete.Count);
            }
        }

        var batchDuration = (DateTime.UtcNow - batchStart).TotalMilliseconds;
        _logger.LogInformation("Batch {BatchNumber} processing complete. Success: {ProcessedCount}, Failed: {FailedCount}, Deleted: {DeletedCount}, Duration: {Duration}ms",
            batchNumber, successMessages.Count, failedMessages.Count, deletedCount, batchDuration);

        return (successMessages.Count, failedMessages.Count, deletedCount);
    }

    /// <summary>
    /// Converts SQS Event record to internal QueueMessage.
    /// </summary>
    private QueueMessage ConvertSqsEventToMessage(SQSEvent.SQSMessage record)
    {
        return new QueueMessage
        {
            MessageId = record.MessageId,
            Body = record.Body,
            ReceiptHandle = record.ReceiptHandle,
            MessageGroupId = GetMessageGroupId(record), // Use helper to extract GroupId
            Attributes = record.Attributes,
            MessageAttributes = ConvertMessageAttributes(record.MessageAttributes)
        };
    }

    /// <summary>
    /// Converts SQS API Message to internal QueueMessage.
    /// </summary>
    private QueueMessage ConvertSqsApiMessageToQueueMessage(Message msg)
    {
        return new QueueMessage
        {
            MessageId = msg.MessageId,
            Body = msg.Body,
            ReceiptHandle = msg.ReceiptHandle,
            MessageGroupId = GetMessageGroupIdFromSqsMessage(msg), // Use helper to extract GroupId
            Attributes = msg.Attributes,
            MessageAttributes = msg.MessageAttributes
        };
    }


    /// <summary>
    /// Extracts MessageGroupId from SQS event message attributes.
    /// </summary>
    private static string GetMessageGroupId(SQSEvent.SQSMessage record)
    {
        // FIFO queues guarantee MessageGroupId in attributes
        if (record.Attributes?.TryGetValue("MessageGroupId", out var groupId) == true && !string.IsNullOrEmpty(groupId))
        {
            return groupId;
        }
        // Fallback for safety, although should not be needed for FIFO
        if (record.MessageAttributes?.TryGetValue("MessageGroupId", out var msgAttr) == true && !string.IsNullOrEmpty(msgAttr.StringValue))
        {
            return msgAttr.StringValue;
        }
        return "UnknownGroup"; // Should not happen with FIFO
    }

    /// <summary>
    /// Extracts MessageGroupId from direct SQS message attributes.
    /// </summary>
    private static string GetMessageGroupIdFromSqsMessage(Message message)
    {
        if (message.Attributes?.TryGetValue("MessageGroupId", out var groupId) == true && !string.IsNullOrEmpty(groupId))
        {
            return groupId;
        }
        return "UnknownGroup"; // Should not happen with FIFO
    }

    /// <summary>
    /// Converts SQS Event MessageAttributes to SDK MessageAttributeValue dictionary.
    /// </summary>
    private Dictionary<string, MessageAttributeValue>? ConvertMessageAttributes(Dictionary<string, SQSEvent.MessageAttribute> eventAttributes)
    {
        if (eventAttributes == null) return null;
        return eventAttributes.ToDictionary(
            kvp => kvp.Key,
            kvp => new MessageAttributeValue
            {
                StringValue = kvp.Value.StringValue,
                BinaryValue = kvp.Value.BinaryValue,
                DataType = kvp.Value.DataType
            }
        );
    }
}