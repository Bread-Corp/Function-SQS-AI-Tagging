using Tender_AI_Tagging_Lambda.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tender_AI_Tagging_Lambda.Interfaces
{
    /// <summary>
    /// Defines the contract for creating tender message objects from raw message data.
    /// This factory interface provides a standardized way to instantiate TenderMessageBase objects
    /// from incoming SQS message payloads, handling deserialization and message type determination.
    /// </summary>
    public interface IMessageFactory
    {
        /// <summary>
        /// Creates a tender message object from the provided message body and group identifier.
        /// This method is responsible for parsing the raw message content and instantiating
        /// the appropriate TenderMessageBase-derived object based on the message structure and type.
        /// </summary>
        /// <param name="messageBody">
        /// The raw message content, typically in JSON format, that contains the tender data
        /// to be deserialized into a message object. This parameter should contain all the
        /// necessary information to construct a valid tender message.
        /// </param>
        /// <param name="messageGroupId">
        /// The message group identifier used for message ordering and grouping in SQS FIFO queues.
        /// This parameter helps identify which group or category the message belongs to,
        /// which may influence the type of message object created or its processing priority.
        /// </param>
        /// <returns>
        /// A TenderMessageBase object if the message was successfully parsed and created,
        /// or null if the message body is invalid, malformed, or represents an unsupported message type.
        /// The specific type of TenderMessageBase returned depends on the content and structure
        /// of the messageBody parameter.
        /// </returns>
        TenderMessageBase? CreateMessage(string messageBody, string messageGroupId);
    }
}
