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
        private IChannel ReceiverChannel { get; set; }

        //=================================================================================== Overrides

        protected override async Task StartMessagePumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                Connection = await OpenConnectionAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error opening connection in RabbitMQ message provider. {ex.Message}");
                return;
            }

            string queueName;
            
            try
            {
                // declare an exchange and bind a queue unique for this application
                using (var initChannel = await OpenChannelAsync(Connection, cancellationToken))
                {
                    await initChannel.ExchangeDeclareAsync(_options.MessageExchange, "fanout",
                        cancellationToken: cancellationToken);

                    // let the server generate a unique queue name
                    queueName = (await initChannel.QueueDeclareAsync(cancellationToken: cancellationToken)).QueueName;
                    _logger.LogTrace($"RMQ: RabbitMQ queue declared: {queueName}");

                    await initChannel.QueueBindAsync(queueName, _options.MessageExchange, string.Empty, cancellationToken: cancellationToken);

                    _logger.LogTrace($"RMQ: RabbitMQ queue {queueName} is bound to exchange {_options.MessageExchange}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"RabbitMQ message provider connection error. Service url: {_options.ServiceUrl}");
                throw;
            }

            // use a single channel for receiving messages
            ReceiverChannel = await OpenChannelAsync(Connection, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(ReceiverChannel);
            consumer.ShutdownAsync += (_, args) =>
            {
                _logger.LogTrace($"RMQ: RabbitMQ consumer shutdown. Cause: {args.Cause ?? "[null]"}");
                return Task.CompletedTask;
            };
            consumer.ReceivedAsync += async (model, args) =>
            {
                var messageLength = args?.Body.Length ?? 0;

                _logger.LogTrace($"Message received. Length: {messageLength}");

                if (messageLength == 0)
                    return;

                // this is the main entry point for receiving messages
                var body = args.Body.ToArray();
                using var ms = new MemoryStream(body);
                OnMessageReceived(ms);
            };

            await ReceiverChannel.BasicConsumeAsync(queueName, true, consumer, cancellationToken: cancellationToken);

            _logger.LogInformation($"RabbitMQ message provider connected to {_options.ServiceUrl}. " +
                                   $"Exchange: {_options.MessageExchange}. QueueName: {queueName}");

            await base.StartMessagePumpAsync(cancellationToken);
        }
        protected override async Task StopMessagePumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (ReceiverChannel != null)
                    await ReceiverChannel.CloseAsync(cancellationToken: cancellationToken);
                if(Connection != null)
                    await Connection.CloseAsync(cancellationToken: cancellationToken);
            }
            catch (ChannelClosedException ex)
            {
                _logger.LogTrace($"RabbitMQ channel closed with an exception: {ex.Message}");
            }

            await base.StopMessagePumpAsync(cancellationToken);
        }

        public override string ReceiverName => "RabbitMQ";

        public override bool RestartingAllChannels => false;
        public override Task RestartAllChannelsAsync(CancellationToken cancellationToken)
        {
            _logger.LogTrace("RMQ: RabbitMQ RestartAllChannels does nothing.");
            return Task.CompletedTask;
        }

        protected override async Task InternalSendAsync(Stream messageBody, bool isDebugMessage, CancellationToken cancel)
        {
            byte[] body;

            try
            {
                if (messageBody?.Length == 0)
                {
                    _logger.LogTrace("RMQ: Empty message body.");
                    return;
                }
                
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error when converting message body to a byte array. {ex.Message}");
                return;
            }

            // Create a channel per send request to avoid sharing channels 
            // between threads and be able to dispose the object.
            try
            {
                _logger.LogTrace($"RMQ: Publishing message. Length: {body.Length}");

                await using var channel = await OpenChannelAsync(Connection, cancel);
                await channel.BasicPublishAsync(_options.MessageExchange, string.Empty, body, cancellationToken: cancel);
                await channel.CloseAsync(cancel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error when sending message on RabbitMq channel: {ex.Message}");
            }
        }

        //=================================================================================== Helper methods

        private async Task<IChannel> OpenChannelAsync(IConnection connection, CancellationToken cancel)
        {
            if (connection == null)
            {
                _logger.LogError("RabbitMq connection is null.");
                throw new ArgumentNullException(nameof(connection));
            }

            var channel = await connection.CreateChannelAsync(cancellationToken: cancel);
            channel.CallbackExceptionAsync += (_, args) =>
            {
                _logger.LogError(args.Exception, $"RMQ: RabbitMQ channel callback exception: {args.Exception?.Message}");
                return Task.CompletedTask;
            };

            return channel;
        }
        private async Task<IConnection> OpenConnectionAsync(CancellationToken cancel)
        {
            var factory = new ConnectionFactory { Uri = new Uri(_options.ServiceUrl), ConsumerDispatchConcurrency = 5 };
            var connection = await factory.CreateConnectionAsync(cancel);
            connection.CallbackExceptionAsync += (_, ea) =>
            {
                _logger.LogError(ea.Exception, $"RMQ: RabbitMQ connection callback exception: {ea.Exception?.Message}");
                return Task.CompletedTask;
            };
            connection.ConnectionShutdownAsync += (_, ea) =>
            {
                _logger.LogTrace("RMQ: RabbitMQ connection shutdown.");
                return Task.CompletedTask;
            };

            return connection;
        }
    }
}
