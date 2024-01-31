using SenseNet.Tools.Configuration;

namespace SenseNet.Messaging.RabbitMQ.Configuration
{
    /// <summary>
    /// Contains options for connecting to RabbitMQ.
    /// </summary>
    [OptionsClass(sectionName: "sensenet:rabbitmq")]
    public class RabbitMqOptions
    {
        /// <summary>
        /// Gets or sets the service url of the RabbitMQ server.
        /// </summary>
        public string ServiceUrl { get; set; } = "amqp://localhost:5672";
        /// <summary>
        /// Gets or sets the name of the message exchange. The default value is 'snmessaging'.
        /// </summary>
        /// <remarks>
        /// This value should be unique for the application. If you have multiple 
        /// applications using the same RabbitMQ server, you should use 
        /// app-specific different values for this property.
        /// If you have multiple instances of the same application using the
        /// same RabbitMQ server, you should use the same value for all instances
        /// so that they can communicate with each other.
        /// </remarks>
        public string MessageExchange { get; set; } = "snmessaging";
    }
}
