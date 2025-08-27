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
	public class RabbitMQMessageProvider(IClusterMessageFormatter formatter, IOptions<ClusterMemberInfo> memberInfo,
		IOptions<RabbitMqOptions> options, ILogger<RabbitMQMessageProvider> _logger) : ClusterChannel(formatter, memberInfo.Value)
	{
		private readonly RabbitMqOptions _options = options.Value;

		//=================================================================================== Shared resources

		private IConnection Connection { get; set; }
		private IChannel ReceiverChannel { get; set; }

		private static int _activeConnections;
		private static int _activeChannels;


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
				if (Connection != null)
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
		public override async Task RestartAllChannelsAsync(CancellationToken cancellationToken)
		{
			_logger.LogInformation("RMQ: Restarting connection and channels...");
			try
			{
				if (ReceiverChannel?.IsOpen == true)
					await ReceiverChannel.CloseAsync(cancellationToken: cancellationToken);
				if (Connection?.IsOpen == true)
					await Connection.CloseAsync(cancellationToken: cancellationToken);

				Connection = await OpenConnectionAsync(cancellationToken);
				// Note: You'll need to recreate the ReceiverChannel and consumer here
				_logger.LogInformation("RMQ: Connection and channels restarted successfully");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to restart connections");
				throw;
			}
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
				throw;
			}

			var retryDelays = new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };
			Exception lastException = null;

			for (int attempt = 0; attempt < retryDelays.Length + 1; attempt++)
			{
				try
				{
					if (Connection?.IsOpen != true)
					{
						_logger.LogWarning("Connection is not open, attempting to reconnect...");
						Connection = await OpenConnectionAsync(cancel);
					}

					_logger.LogTrace($"RMQ: Publishing message. Length: {body.Length}, Attempt: {attempt + 1}");

					await using var channel = await OpenChannelAsync(Connection, cancel);
					await channel.BasicPublishAsync(_options.MessageExchange, string.Empty, body, cancellationToken: cancel);
					return;
				}
				catch (Exception ex) when (IsRetriableException(ex) && attempt < retryDelays.Length)
				{
					lastException = ex;
					_logger.LogWarning(ex, $"Send attempt {attempt + 1} failed, retrying in {retryDelays[attempt].TotalMilliseconds}ms: {ex.Message}");
					await Task.Delay(retryDelays[attempt], cancel);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, $"Error when sending message on RabbitMq channel: {ex.Message}");
					throw;
				}
			}

			// If we get here, all retries failed
			_logger.LogError(lastException, "Failed to send message after all retry attempts");
			throw new InvalidOperationException("Failed to send message after retries", lastException);
		}

		private static bool IsRetriableException(Exception ex)
		{
			var exceptionType = ex.GetType().Name;
			return exceptionType == "AlreadyClosedException" ||
				   exceptionType == "BrokerUnreachableException" ||
				   exceptionType == "ConnectFailureException" ||
				   ex is IOException ||
				   ex is System.Net.Sockets.SocketException ||
				   ex.Message.Contains("forcibly closed") ||
				   ex.Message.Contains("Unable to read data from the transport connection");
		}

		//=================================================================================== Helper methods

		private async Task<IChannel> OpenChannelAsync(IConnection connection, CancellationToken cancel)
		{
			if (connection == null)
			{
				_logger.LogError("RabbitMq connection is null.");
				throw new ArgumentNullException(nameof(connection));
			}

			if (!connection.IsOpen)
			{
				_logger.LogError("RabbitMq connection is not open.");
				throw new InvalidOperationException("Cannot create channel on closed connection");
			}

			var channel = await connection.CreateChannelAsync(cancellationToken: cancel);
			channel.CallbackExceptionAsync += (_, args) =>
			{
				_logger.LogError(args.Exception, $"RMQ: RabbitMQ channel callback exception: {args.Exception?.Message}");
				return Task.CompletedTask;
			};
			channel.ChannelShutdownAsync += (_, args) =>
			{
				Interlocked.Decrement(ref _activeChannels);
				_logger.LogInformation($"Channel closed. Active channels: {_activeChannels}");
				return Task.CompletedTask;
			};

			Interlocked.Increment(ref _activeChannels);
			_logger.LogInformation($"Opening new channel. Active channels: {_activeChannels}");

			return channel;
		}

		private async Task<IConnection> OpenConnectionAsync(CancellationToken cancel)
		{
			var factory = new ConnectionFactory
			{
				Uri = new Uri(_options.ServiceUrl),
				ConsumerDispatchConcurrency = 5,
				AutomaticRecoveryEnabled = true,
				NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
				RequestedHeartbeat = TimeSpan.FromSeconds(60)
			};

			var connection = await factory.CreateConnectionAsync(cancel);
			connection.CallbackExceptionAsync += (_, ea) =>
			{
				_logger.LogError(ea.Exception, $"RMQ: RabbitMQ connection callback exception: {ea.Exception?.Message}");
				return Task.CompletedTask;
			};
			connection.ConnectionShutdownAsync += (_, ea) =>
			{
				Interlocked.Decrement(ref _activeConnections);
				_logger.LogInformation($"Connection shutdown. Active connections: {_activeConnections}");
				_logger.LogTrace("RMQ: RabbitMQ connection shutdown.");
				return Task.CompletedTask;
			};

			Interlocked.Increment(ref _activeConnections);
			_logger.LogInformation($"Opening new connection. Active connections: {_activeConnections}");
		
			return connection;
		}
		public (int Connections, int Channels) GetPoolStatus() => (_activeConnections, _activeChannels);
	}
}
