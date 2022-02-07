namespace SenseNet.Messaging.RabbitMQ.Configuration
{
    public class RabbitMqOptions
    {
        public string ServiceUrl { get; set; } = "amqp://localhost:5672";
        public string MessageExchange { get; set; } = "snmessaging";
    }
}
