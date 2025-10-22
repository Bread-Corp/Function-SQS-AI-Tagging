using Microsoft.Extensions.Logging;
using Tender_AI_Tagging_Lambda.Interfaces;
using Tender_AI_Tagging_Lambda.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Services
{
    /// <summary>
    /// Factory service for creating tender message objects from JSON message bodies
    /// </summary>
    public class MessageFactory : IMessageFactory
    {
        private readonly ILogger<MessageFactory> _logger;
        private readonly JsonSerializerOptions _jsonOptions;

        public MessageFactory(ILogger<MessageFactory> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Configure JSON deserialization options for consistent message parsing
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,  // Handle camelCase JSON properties
                PropertyNameCaseInsensitive = true                  // Allow case-insensitive property matching
            };
        }

        /// <summary>
        /// Creates appropriate tender message object based on message group ID and JSON body
        /// </summary>
        public TenderMessageBase? CreateMessage(string messageBody, string messageGroupId)
        {
            // Validate input parameters
            if (string.IsNullOrWhiteSpace(messageBody))
            {
                _logger.LogWarning("Empty or null message body provided for MessageGroupId: {MessageGroupId}", messageGroupId ?? "NULL");
                return null;
            }

            if (string.IsNullOrWhiteSpace(messageGroupId))
            {
                _logger.LogWarning("Empty or null MessageGroupId provided");
                return null;
            }

            var bodyLength = messageBody.Length;
            var groupIdLower = messageGroupId.ToLowerInvariant();

            _logger.LogInformation("Creating message - GroupId: {MessageGroupId}, BodyLength: {BodyLength}",
                messageGroupId, bodyLength);

            try
            {
                // Route to appropriate message type based on message group ID
                var result = groupIdLower switch
                {
                    // eTender message group IDs - handle both scraper and lambda sources
                    "etenderscrape" or "etenderlambda"or "etender" or "etenders" => CreateETenderMessage(messageBody, messageGroupId),

                    // Eskom message group IDs - handle both scraper and lambda sources
                    "eskomtenderscrape" or "eskomlambda"or "eskom" => CreateEskomTenderMessage(messageBody, messageGroupId),

                    // Transnet message group IDs - handle both scraper and lambda sources
                    "transnettenderscrape" or "transnetlambda"or "transnet" => CreateTransnetTenderMessage(messageBody, messageGroupId),

                    // SARS message group IDs
                    "sarstenderscrape" or "sarslambda" or "sars" => CreateSarsTenderMessage(messageBody, messageGroupId),

                    // SANRAL message group IDs
                    "Sanraltenderscrape" or "sanrallambda" or "sanral" => CreateSanralTenderMessage(messageBody, messageGroupId),

                    // Unsupported message group ID
                    _ => HandleUnsupportedMessageGroup(messageGroupId)
                };

                // Validate the created message before returning
                if (result != null)
                {
                    var messageType = result.GetType().Name;
                    var sourceType = result.GetSourceType();
                    var tenderNumber = result.TenderNumber ?? "Unknown";

                    _logger.LogInformation("Message created successfully - Type: {MessageType}, Source: {SourceType}, TenderNumber: {TenderNumber}, GroupId: {MessageGroupId}",
                        messageType, sourceType, tenderNumber, messageGroupId);
                }
                else
                {
                    _logger.LogWarning("Message creation returned null - GroupId: {MessageGroupId}, BodyLength: {BodyLength}",
                        messageGroupId, bodyLength);
                }

                return result;
            }
            catch (JsonException jsonEx)
            {
                // Log JSON deserialization errors with specific details
                _logger.LogError(jsonEx, "JSON deserialization failed - GroupId: {MessageGroupId}, BodyLength: {BodyLength}, Error: {ErrorMessage}",
                    messageGroupId, bodyLength, jsonEx.Message);
                return null;
            }
            catch (NotSupportedException notSupportedEx)
            {
                // Log unsupported message group IDs
                _logger.LogWarning(notSupportedEx, "Unsupported message group - GroupId: {MessageGroupId}", messageGroupId);
                return null;
            }
            catch (ArgumentException argEx)
            {
                // Handle argument-related errors during deserialization
                _logger.LogError(argEx, "Invalid argument during message creation - GroupId: {MessageGroupId}, Error: {ErrorMessage}",
                    messageGroupId, argEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                // Log unexpected errors during message creation
                _logger.LogError(ex, "Unexpected error during message creation - GroupId: {MessageGroupId}, BodyLength: {BodyLength}",
                    messageGroupId, bodyLength);
                return null;
            }
        }

        /// <summary>
        /// Creates ETender message from JSON body with null safety
        /// </summary>
        private ETenderMessage? CreateETenderMessage(string messageBody, string messageGroupId)
        {
            _logger.LogDebug("Deserializing eTender message - GroupId: {MessageGroupId}", messageGroupId);

            try
            {
                // Attempt JSON deserialization with null safety
                var message = JsonSerializer.Deserialize<ETenderMessage>(messageBody, _jsonOptions);

                // Validate deserialized message
                if (message == null)
                {
                    _logger.LogWarning("eTender deserialization returned null - GroupId: {MessageGroupId}", messageGroupId);
                    return null;
                }

                // Log successful deserialization with safe property access
                _logger.LogDebug("eTender message deserialized successfully - ID: {TenderId}, Status: {Status}, Title: {Title}",
                    message.Id, message.Status ?? "Unknown", message.Title ?? "No Title");

                return message;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to deserialize eTender message - GroupId: {MessageGroupId}, Error: {ErrorMessage}",
                    messageGroupId, jsonEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating eTender message - GroupId: {MessageGroupId}",
                    messageGroupId);
                return null;
            }
        }

        /// <summary>
        /// Creates Eskom tender message from JSON body with null safety
        /// </summary>
        private EskomTenderMessage? CreateEskomTenderMessage(string messageBody, string messageGroupId)
        {
            _logger.LogDebug("Deserializing Eskom message - GroupId: {MessageGroupId}", messageGroupId);

            try
            {
                // Attempt JSON deserialization with null safety
                var message = JsonSerializer.Deserialize<EskomTenderMessage>(messageBody, _jsonOptions);

                // Validate deserialized message
                if (message == null)
                {
                    _logger.LogWarning("Eskom deserialization returned null - GroupId: {MessageGroupId}", messageGroupId);
                    return null;
                }

                // Log successful deserialization with safe property access
                _logger.LogDebug("Eskom message deserialized successfully - TenderNumber: {TenderNumber}, Source: {Source}, Title: {Title}",
                    message.TenderNumber ?? "Unknown", message.Source ?? "Unknown", message.Title ?? "No Title");

                return message;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to deserialize Eskom message - GroupId: {MessageGroupId}, Error: {ErrorMessage}",
                    messageGroupId, jsonEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating Eskom message - GroupId: {MessageGroupId}",
                    messageGroupId);
                return null;
            }
        }

        /// <summary>
        /// Creates Transnet tender message from JSON body with null safety
        /// </summary>
        private TransnetTenderMessage? CreateTransnetTenderMessage(string messageBody, string messageGroupId)
        {
            _logger.LogDebug("Deserializing Transnet message - GroupId: {MessageGroupId}", messageGroupId);

            try
            {
                // Attempt JSON deserialization with null safety
                var message = JsonSerializer.Deserialize<TransnetTenderMessage>(messageBody, _jsonOptions);

                // Validate deserialized message
                if (message == null)
                {
                    _logger.LogWarning("Transnet deserialization returned null - GroupId: {MessageGroupId}", messageGroupId);
                    return null;
                }

                // Log successful deserialization with safe property access
                _logger.LogDebug("Transnet message deserialized successfully - TenderNumber: {TenderNumber}, Source: {Source}, Title: {Title}",
                    message.TenderNumber ?? "Unknown", message.Source ?? "Unknown", message.Title ?? "No Title");

                return message;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to deserialize Transnet message - GroupId: {MessageGroupId}, Error: {ErrorMessage}",
                    messageGroupId, jsonEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating Transnet message - GroupId: {MessageGroupId}",
                    messageGroupId);
                return null;
            }
        }

        /// <summary>
        /// Creates SARS tender message from JSON body with null safety.
        /// </summary>
        private SarsTenderMessage? CreateSarsTenderMessage(string messageBody, string messageGroupId)
        {
            _logger.LogDebug("Deserializing SARS message - GroupId: {MessageGroupId}", messageGroupId);
            try
            {
                var message = JsonSerializer.Deserialize<SarsTenderMessage>(messageBody, _jsonOptions);
                if (message == null)
                {
                    _logger.LogWarning("SARS deserialization returned null - GroupId: {MessageGroupId}", messageGroupId);
                    return null;
                }
                _logger.LogDebug("SARS message deserialized successfully - TenderNumber: {TenderNumber}, Title: {Title}",
                    message.TenderNumber ?? "Unknown", message.Title ?? "No Title");
                return message;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to deserialize SARS message - GroupId: {MessageGroupId}, Error: {ErrorMessage}", messageGroupId, jsonEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating SARS message - GroupId: {MessageGroupId}", messageGroupId);
                return null;
            }
        }

        /// <summary>
        /// Creates SANRAL tender message from JSON body with null safety.
        /// </summary>
        private SanralTenderMessage? CreateSanralTenderMessage(string messageBody, string messageGroupId)
        {
            _logger.LogDebug("Deserializing SANRAL message - GroupId: {MessageGroupId}", messageGroupId);
            try
            {
                var message = JsonSerializer.Deserialize<SanralTenderMessage>(messageBody, _jsonOptions);
                if (message == null)
                {
                    _logger.LogWarning("SANRAL deserialization returned null - GroupId: {MessageGroupId}", messageGroupId);
                    return null;
                }
                _logger.LogDebug("SANRAL message deserialized successfully - TenderNumber: {TenderNumber}, Title: {Title}",
                    message.TenderNumber ?? "Unknown", message.Title ?? "No Title");
                return message;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Failed to deserialize SANRAL message - GroupId: {MessageGroupId}, Error: {ErrorMessage}", messageGroupId, jsonEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating SANRAL message - GroupId: {MessageGroupId}", messageGroupId);
                return null;
            }
        }

        /// <summary>
        /// Handles unsupported message group IDs by throwing appropriate exception
        /// </summary>
        private TenderMessageBase? HandleUnsupportedMessageGroup(string messageGroupId)
        {
            var errorMessage = $"Unsupported MessageGroupId: {messageGroupId}";

            _logger.LogWarning("Unsupported message group encountered - GroupId: {MessageGroupId}, SupportedGroups: etenderscrape, etenderlambda, eskomtenderscrape, eskomlambda",
                messageGroupId);

            // Throw exception to be caught by main method's exception handling
            throw new NotSupportedException(errorMessage);
        }
    }
}
