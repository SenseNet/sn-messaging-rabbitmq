using System;
using SenseNet.Communication.Messaging;
using System.IO;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SenseNet.Messaging.RabbitMQ.Configuration;
using System.Threading.Channels;

namespace SenseNet.Messaging.RabbitMQ
{
    // ReSharper disable once InconsistentNaming
    public class RabbitMQMessageProvider : ClusterChannel
    {
        private readonly ILogger<RabbitMQMessageProvider> _logger;
        private readonly RabbitMqOptions _options;

        //=================================================================================== Constructors

        public RabbitMQMessageProvider(IClusterMessageFormatter formatter, IOptions<ClusterMemberInfo> memberInfo,
            IOptions<RabbitMqOptions> options, ILogger<RabbitMQMessageProvider> logger) : base(formatter, memberInfo.Value)
        {
            _logger = logger;
            _options = options.Value;
        }

        //=================================================================================== Shared resources

        private IConnection Connection { get; set; }
        private IModel ReceiverChannel { get; set; }

        //=================================================================================== Overrides

        protected override Task StartMessagePumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                Connection = OpenConnection();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error opening connection in RabbitMQ message provider. {ex.Message}");
                return Task.CompletedTask;
            }

            string queueName;
            
            try
            {
                // declare an exchange and bind a queue unique for this application
                using (var initChannel = OpenChannel(Connection))
                {
                    initChannel.ExchangeDeclare(_options.MessageExchange, "fanout");

                    // let the server generate a unique queue name
                    queueName = initChannel.QueueDeclare().QueueName;
                    _logger.LogTrace($"RMQ: RabbitMQ queue declared: {queueName}");

                    initChannel.QueueBind(queueName, _options.MessageExchange, string.Empty);

                    _logger.LogTrace($"RMQ: RabbitMQ queue {queueName} is bound to exchange {_options.MessageExchange}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"RabbitMQ message provider connection error. Service url: {_options.ServiceUrl}");
                throw;
            }

            // use a single channel for receiving messages
            ReceiverChannel = OpenChannel(Connection);

            var consumer = new EventingBasicConsumer(ReceiverChannel);
            consumer.Shutdown += (sender, args) => { _logger.LogTrace("RMQ: RabbitMQ consumer shutdown."); };
            consumer.ConsumerCancelled += (sender, args) => { _logger.LogTrace("RMQ: RabbitMQ consumer cancelled."); };
            consumer.Received += (model, args) =>
            {
                _logger.LogTrace($"Message received. Length: {args?.Body?.Length ?? 0}");

                // this is the main entry point for receiving messages
                using (var ms = new MemoryStream(args.Body))
                {
                    OnMessageReceived(ms);
                }
            };

            ReceiverChannel.BasicConsume(queueName, true, consumer);

            _logger.LogInformation($"RabbitMQ message provider connected to {_options.ServiceUrl}. " +
                                   $"Exchange: {_options.MessageExchange}. Queuename: {queueName}");

            return base.StartMessagePumpAsync(cancellationToken);
        }
        protected override Task StopMessagePumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                ReceiverChannel?.Close();
                Connection?.Close();
            }
            catch (ChannelClosedException ex)
            {
                _logger.LogTrace($"RabbitMQ channel closed with an exception: {ex.Message}");
            }

            return base.StopMessagePumpAsync(cancellationToken);
        }

        public override string ReceiverName { get; } = "RabbitMQ";

        public override bool RestartingAllChannels => false;
        public override Task RestartAllChannelsAsync(CancellationToken cancellationToken)
        {
            _logger.LogTrace("RMQ: RabbitMQ RestartAllChannels does nothing.");
            return Task.CompletedTask;
        }

        protected override Task InternalSendAsync(Stream messageBody, bool isDebugMessage, CancellationToken cancel)
        {
            if (messageBody?.Length == 0)
            {
                _logger.LogTrace("RMQ: Empty message body.");
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

            // we do not have to wait for the publish operation to finish
            Task.Run(() =>
            {
                // Create a channel per send request to avoid sharing channels 
                // between threads and be able to dispose the object.
                try
                {
                    _logger.LogTrace($"RMQ: Publishing message. Length: {body.Length}");

                    using (var channel = OpenChannel(Connection))
                    {
                        channel.BasicPublish(_options.MessageExchange, string.Empty, null, body);
                        channel.Close();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error when sending message on RabbitMq channel: {ex.Message}");
                }
            }, cancel).ConfigureAwait(false);

            return Task.CompletedTask;
        }

        //=================================================================================== Helper methods

        private IModel OpenChannel(IConnection connection)
        {
            if (connection == null)
            {
                _logger.LogError("RabbitMq connection is null.");
                throw new ArgumentNullException(nameof(connection));
            }

            var channel = connection.CreateModel();
            channel.CallbackException += (sender, args) =>
            {
                _logger.LogError(args.Exception, $"RMQ: RabbitMQ channel callback exception: {args.Exception?.Message}");
            };

            return channel;
        }
        private IConnection OpenConnection()
        {
            var factory = new ConnectionFactory { Uri = new Uri(_options.ServiceUrl) };
            var connection = factory.CreateConnection();
            connection.CallbackException += (sender, ea) =>
            {
                _logger.LogError(ea.Exception, $"RMQ: RabbitMQ connection callback exception: {ea.Exception?.Message}");
            };
            connection.ConnectionShutdown += (sender, ea) =>
            {
                _logger.LogTrace("RMQ: RabbitMQ connection shutdown.");
            };

            return connection;
        }
    }
}
