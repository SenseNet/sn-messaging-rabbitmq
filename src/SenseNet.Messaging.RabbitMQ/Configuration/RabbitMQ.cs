using SenseNet.Configuration;

namespace SenseNet.Messaging.RabbitMQ.Configuration
{
    // ReSharper disable once InconsistentNaming
    public class RabbitMQ : SnConfig
    {
        private const string SectionName = "sensenet/rabbitmq";

        public static string ServiceUrl { get; internal set; } = GetString(SectionName, "ServiceUrl", "amqp://localhost:5672");
        public static string MessageExchange { get; internal set; } = GetString(SectionName, "MessageExchange", "snmessaging");
    }
}
