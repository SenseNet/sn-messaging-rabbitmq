using System;
using System.Collections.Generic;
using SenseNet.Communication.Messaging;
using System.IO;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SenseNet.Diagnostics;

namespace SenseNet.Messaging.RabbitMQ
{
    // ReSharper disable once InconsistentNaming
    public class RabbitMQMessageProvider : ClusterChannel
    {
        //=================================================================================== Constructors

        public RabbitMQMessageProvider(IClusterMessageFormatter formatter, ClusterMemberInfo memberInfo) : base(formatter, memberInfo) { }

        //=================================================================================== Shared recources

        private IConnection Connection { get; } = OpenConnection();
        private IModel ReceiverChannel { get; set; }

        //=================================================================================== Overrides

        protected override void StartMessagePump()
        {
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

            base.StartMessagePump();
        }
        protected override void StopMessagePump()
        {
            ReceiverChannel?.Close();

            base.StopMessagePump();
        }

        public override string ReceiverName { get; } = "RabbitMQ";

        public override bool RestartingAllChannels => false;
        public override void RestartAllChannels()
        {
            SnTrace.Messaging.Write("RMQ: RabbitMQ RestartAllChannels does nothing.");
        }
        
        protected override void InternalSend(Stream messageBody, bool isDebugMessage)
        {
            if (messageBody?.Length == 0)
            {
                SnTrace.Messaging.WriteError("RMQ: Empty message body.");
                return;
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
                    using (var op1 = SnTrace.Messaging.StartOperation("InternalSend"))
                    {
                        using (var channel = OpenChannel(Connection))
                        {
                            using (var op2 = SnTrace.Messaging.StartOperation("BasicPublish"))
                            {
                                channel.BasicPublish(Configuration.RabbitMQ.MessageExchange, string.Empty, null, body);
                                op2.Successful = true;
                            }

                            channel.Close();
                        }

                        op1.Successful = true;
                    }
                }
                catch (Exception ex)
                {
                    SnLog.WriteException(ex);
                    SnTrace.Messaging.WriteError($"SEND ERROR {ex.Message}");
                }
            });
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

            SnTrace.Messaging.Write("RMQ: RabbitMQ channel opened.");

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
