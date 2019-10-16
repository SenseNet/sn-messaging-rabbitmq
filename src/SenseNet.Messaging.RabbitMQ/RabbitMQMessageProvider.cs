using System;
using System.Collections.Generic;
using SenseNet.Communication.Messaging;
using System.IO;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SenseNet.Diagnostics;
using System.Threading;

namespace SenseNet.Messaging.RabbitMQ
{
    // ReSharper disable once InconsistentNaming
    public class RabbitMQMessageProvider : ClusterChannel
    {
        //=================================================================================== Constructors

        public RabbitMQMessageProvider(IClusterMessageFormatter formatter, ClusterMemberInfo memberInfo) : base(formatter, memberInfo) { }

        //=================================================================================== Shared recources

        private IConnection Connection { get; set; }
        private IModel ReceiverChannel { get; set; }

        //=================================================================================== Overrides

        protected override Task StartMessagePumpAsync(CancellationToken cancellationToken)
        {
            Connection = OpenConnection();

            string queueName;
            
            try
            {
                // declare an exchange and bind a queue unique for this application
                using (var initChannel = OpenChannel(Connection))
                {
                    initChannel.ExchangeDeclare(Configuration.RabbitMQ.MessageExchange, "fanout");

                    // let the server generate a unique queue name
                    queueName = initChannel.QueueDeclare().QueueName;
                    SnTrace.Messaging.Write($"RMQ: RabbitMQ queue declared: {queueName}");

                    initChannel.QueueBind(queueName, Configuration.RabbitMQ.MessageExchange, string.Empty);

                    SnTrace.Messaging.Write($"RMQ: RabbitMQ queue {queueName} is bound to exchange {Configuration.RabbitMQ.MessageExchange}.");
                }
            }
            catch (Exception ex)
            {
                SnLog.WriteException(ex, $"RabbitMQ message provider connection error. Service url: {Configuration.RabbitMQ.ServiceUrl}");
                throw;
            }

            // use a single channel for receiving messages
            ReceiverChannel = OpenChannel(Connection);

            var consumer = new EventingBasicConsumer(ReceiverChannel);
            consumer.Shutdown += (sender, args) => { SnTrace.Messaging.Write("RMQ: RabbitMQ consumer shutdown."); };
            consumer.ConsumerCancelled += (sender, args) => { SnTrace.Messaging.Write("RMQ: RabbitMQ consumer cancelled."); };
            consumer.Received += (model, args) =>
            {
                // this is the main entry point for receiving messages
                using (var ms = new MemoryStream(args.Body))
                {
                    OnMessageReceived(ms);
                }
            };

            ReceiverChannel.BasicConsume(queueName, true, consumer);

            SnLog.WriteInformation($"RabbitMQ message provider connected to {Configuration.RabbitMQ.ServiceUrl}",
                properties: new Dictionary<string, object>
                {
                    { "Exchange", Configuration.RabbitMQ.MessageExchange },
                    { "QueueName", queueName }
                });

            return base.StartMessagePumpAsync(cancellationToken);
        }
        protected override Task StopMessagePumpAsync(CancellationToken cancellationToken)
        {
            ReceiverChannel?.Close();
            Connection?.Close();

            return base.StopMessagePumpAsync(cancellationToken);
        }

        public override string ReceiverName { get; } = "RabbitMQ";

        public override bool RestartingAllChannels => false;
        public override Task RestartAllChannelsAsync(CancellationToken cancellationToken)
        {
            SnTrace.Messaging.Write("RMQ: RabbitMQ RestartAllChannels does nothing.");
            return Task.CompletedTask;
        }

        protected override Task InternalSendAsync(Stream messageBody, bool isDebugMessage, CancellationToken cancellationToken)
        {
            if (messageBody?.Length == 0)
            {
                SnTrace.Messaging.WriteError("RMQ: Empty message body.");
                return Task.CompletedTask;
            }

            byte[] body;
            if (messageBody is MemoryStream ms)
            {
                body = ms.ToArray();
            }
            else
            {
                using (var memoryStream = new MemoryStream())
                {
                    messageBody?.CopyTo(memoryStream);
                    body = memoryStream.ToArray();
                }
            }

            Task.Run(() =>
            {
                // Create a channel per send request to avoid sharing channels 
                // between threads and be able to dispose the object.
                try
                {
                    using (var channel = OpenChannel(Connection))
                    {
                        channel.BasicPublish(Configuration.RabbitMQ.MessageExchange, string.Empty, null, body);
                        channel.Close();
                    }
                }
                catch (Exception ex)
                {
                    SnLog.WriteException(ex);
                    SnTrace.Messaging.WriteError($"SEND ERROR {ex.Message}");
                }
            }).ConfigureAwait(false);

            return Task.CompletedTask;
        }

        //=================================================================================== Helper methods

        private static IModel OpenChannel(IConnection connection)
        {
            var channel = connection.CreateModel();
            channel.CallbackException += (sender, args) =>
            {
                SnLog.WriteException(args.Exception);
                SnTrace.Messaging.WriteError($"RMQ: RabbitMQ channel callback exception: {args.Exception?.Message}");
            };

            return channel;
        }
        private static IConnection OpenConnection()
        {
            var factory = new ConnectionFactory { Uri = new Uri(Configuration.RabbitMQ.ServiceUrl) };
            var connection = factory.CreateConnection();
            connection.CallbackException += (sender, ea) =>
            {
                SnLog.WriteException(ea.Exception);
                SnTrace.Messaging.WriteError($"RMQ: RabbitMQ connection callback exception: {ea.Exception?.Message}");
            };
            connection.ConnectionShutdown += (sender, ea) =>
            {
                SnTrace.Messaging.Write("RMQ: RabbitMQ connection shutdown.");
            };

            return connection;
        }
    }
}
