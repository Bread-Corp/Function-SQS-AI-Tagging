using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Interfaces
{
    /// <summary>
    /// Defines the contract for Amazon SQS (Simple Queue Service) operations.
    /// This interface provides asynchronous methods for sending and deleting messages
    /// from SQS queues, supporting both single message and batch operations for efficiency.
    /// </summary>
    public interface ISqsService
    {
        /// <summary>
        /// Asynchronously sends a single message to the specified SQS queue.
        /// </summary>
        Task SendMessageAsync(string queueUrl, object message);

        /// <summary>
        /// Asynchronously sends multiple messages to the specified SQS queue in a single batch operation.
        /// </summary>
        Task SendMessageBatchAsync(string queueUrl, List<object> messages);

        /// <summary>
        /// Asynchronously deletes a single message from the specified SQS queue.
        /// </summary>
        Task DeleteMessageAsync(string queueUrl, string receiptHandle);

        /// <summary>
        /// Asynchronously deletes multiple messages from the specified SQS queue in a single batch operation.
        /// </summary>
        Task DeleteMessageBatchAsync(string queueUrl, List<(string id, string receiptHandle)> messages);
    }
}
