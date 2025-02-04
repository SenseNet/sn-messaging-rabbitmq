using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SenseNet.Communication.Messaging;
using SenseNet.Diagnostics;
using SenseNet.Extensions.DependencyInjection;

namespace Tester;

public class Program
{
    public static void Main(string[] args)
    {
        SnTrace.EnableAll();
        var host = CreateHostBuilder(args).Build();

        var channel = host.Services.GetRequiredService<IClusterChannel>();
        channel.MessageReceived += (_, receivedArgs) =>
        {
            var sender = receivedArgs.Message.SenderInfo;
            var message = ((TestDistributedActivity)receivedArgs.Message).Message;
            if (sender.InstanceID != channel.ClusterMemberInfo.InstanceID)
                Console.WriteLine($"Received: from {sender.InstanceID}: '{message}'");
        };

        channel.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        channel.AllowMessageProcessing = true;

        Task.Delay(10).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("RabbitMQMessageProvider Tester");
        Console.WriteLine("------------------------------");
        Console.WriteLine();
        Console.WriteLine("Start two or more Tester.exe instance. Write a simple text message under the prompt.");
        Console.WriteLine("Expectation: other instances need to receive the written message.");
        Console.WriteLine("Type 'exit<enter>' to quit.");
        Console.WriteLine();
        Console.WriteLine("InstanceId: " + channel.ClusterMemberInfo.InstanceID);
        while (true)
        {
            var command = Console.ReadLine();
            if (command == "exit")
                break;

            channel.SendAsync(new TestDistributedActivity { Message = command}, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("ok");
        }

        channel.ShutDownAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(builder => builder
                .AddJsonFile("appsettings.json", true, true)
                .AddUserSecrets<Program>()
            )
            .ConfigureServices((hostBuilderContext, services) =>
            {
                services
                    .AddClusterMessageType<TestDistributedActivity>()
                    .AddRabbitMqMessageProvider(configureRabbitMq: options =>
                    {
                        hostBuilderContext.Configuration.GetSection("sensenet:rabbitmq").Bind(options);
                    });
            });
}