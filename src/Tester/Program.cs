using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SenseNet.Communication.Messaging;
using SenseNet.Diagnostics;
using SenseNet.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Tester;

public class Program
{
    private static readonly TestMetrics _metrics = new TestMetrics();
    private static IClusterChannel _channel = null!;
    private static Timer _metricsTimer = null!;

    public static async Task Main(string[] args)
    {
        SnTrace.EnableAll();
        var host = CreateHostBuilder(args).Build();
        _channel = host.Services.GetRequiredService<IClusterChannel>();

        // Start metrics collection
        _metricsTimer = new Timer(PrintMetrics, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        _channel.MessageReceived += (_, receivedArgs) =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var sender = receivedArgs.Message.SenderInfo;
                var message = ((TestDistributedActivity)receivedArgs.Message).Message;

                if (sender.InstanceID != _channel.ClusterMemberInfo.InstanceID)
                {
                    _metrics.IncrementMessagesReceived();

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Received from {sender.InstanceID}: '{message}' (processed in {stopwatch.ElapsedMilliseconds}ms)");
                    _metrics.IncrementMessagesProcessed();
                }
            }
            catch (Exception ex)
            {
                _metrics.IncrementMessageErrors();
                Console.WriteLine($"[ERROR] Message processing failed: {ex.Message}");
            }
            finally
            {
                stopwatch.Stop();
                _metrics.RecordProcessingTime(stopwatch.ElapsedMilliseconds);
            }
        };

        _channel.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _channel.AllowMessageProcessing = true;

        Console.WriteLine();
        Console.WriteLine("Enhanced RabbitMQMessageProvider Tester with Metrics");
        Console.WriteLine("===================================================");
        Console.WriteLine();
        Console.WriteLine("This version measures:");
        Console.WriteLine("- Message loss during failures");
        Console.WriteLine("- Connection recovery time");
        Console.WriteLine("- Resource leak detection");
        Console.WriteLine("- Performance under load");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  exit - quit the application");
        Console.WriteLine("  status - show connection pool status and metrics");
        Console.WriteLine("  metrics - show detailed metrics");
        Console.WriteLine("  rmqstatus - show detailed RabbitMQ resource status");
        Console.WriteLine("  stress <count> - send many messages quickly to test resource usage");
        Console.WriteLine("  autotest - run automated resource leak detection test");
        Console.WriteLine("  crash - simulate application crash during message processing");
        Console.WriteLine("  disconnect - simulate network disconnect (if RabbitMQ supports it)");
        Console.WriteLine("  <text> <count> - send multiple messages");
        Console.WriteLine();
        Console.WriteLine("InstanceId: " + _channel.ClusterMemberInfo.InstanceID);

        while (true)
        {
            var command = Console.ReadLine();
            if (command == "exit")
                break;

            if (command == "status")
            {
                ShowStatus();
                continue;
            }

            if (command == "metrics")
            {
                ShowDetailedMetrics();
                continue;
            }

            if (command == "rmqstatus")
            {
                await ShowRabbitMQResourceStatus();
                continue;
            }

            if (command?.StartsWith("stress ") == true && int.TryParse(command.Split(' ')[1], out var stressCount))
            {
                await RunStressTest(stressCount);
                continue;
            }

            if (command == "autotest")
            {
                await RunAutomatedResourceLeakTest();
                continue;
            }

            if (command == "crash")
            {
                SimulateCrash();
                continue;
            }

            if (command == "disconnect")
            {
                SimulateDisconnect();
                continue;
            }

            if (command?.Split(" ") is [string text, string countString] && int.TryParse(countString, out var count))
            {
                await SendMultipleMessages(text, count);
                continue;
            }

            await SendSingleMessage(command ?? "");
        }

        _metricsTimer?.Dispose();
        _channel.ShutDownAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static async Task SendSingleMessage(string message)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await _channel.SendAsync(new TestDistributedActivity { Message = message }, CancellationToken.None);
            stopwatch.Stop();

            _metrics.IncrementMessagesSent();
            _metrics.RecordSendTime(stopwatch.ElapsedMilliseconds);
            Console.WriteLine($"Message sent in {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            _metrics.IncrementSendErrors();
            Console.WriteLine($"[ERROR] Failed to send message: {ex.Message}");
        }
    }

    private static async Task SendMultipleMessages(string text, int count)
    {
        Console.WriteLine($"Sending {count} messages...");
        var stopwatch = Stopwatch.StartNew();
        var tasks = new List<Task>();

        for (var i = 0; i < count; i++)
        {
            var messageIndex = i; // Capture for closure
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await _channel.SendAsync(new TestDistributedActivity { Message = $"{messageIndex} {text}" }, default);
                    _metrics.IncrementMessagesSent();
                }
                catch (Exception ex)
                {
                    _metrics.IncrementSendErrors();
                    Console.WriteLine($"[ERROR] Failed to send message {messageIndex}: {ex.Message}");
                }
            }));
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        Console.WriteLine($"Sent {count} messages in {stopwatch.ElapsedMilliseconds}ms ({(double)count / stopwatch.ElapsedMilliseconds * 1000:F1} msg/sec)");
    }

    private static async Task RunAutomatedResourceLeakTest()
    {
        Console.WriteLine("=== AUTOMATED RESOURCE LEAK DETECTION TEST ===");
        Console.WriteLine("This test will run for 2 minutes and automatically detect resource leaks.");
        Console.WriteLine("Press 'q' at any time to stop the test early.\n");

        var testDuration = TimeSpan.FromMinutes(2);
        var testInterval = TimeSpan.FromSeconds(10);
        var messagesPerBurst = 1000;

        var testStartTime = DateTime.Now;
        var resourceHistory = new List<ResourceSnapshot>();
        var testCancellation = new CancellationTokenSource();

        // Monitor for 'q' key press to stop test
        var keyMonitorTask = Task.Run(() =>
        {
            while (!testCancellation.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    {
                        testCancellation.Cancel();
                        break;
                    }
                }
                Thread.Sleep(100);
            }
        });

        Console.WriteLine($"Starting automated test - sending {messagesPerBurst} messages every {testInterval.TotalSeconds} seconds");
        Console.WriteLine("Monitoring: Connections, Channels, Memory Usage, GC Collections\n");

        var testNumber = 0;

        try
        {
            while (DateTime.Now - testStartTime < testDuration && !testCancellation.Token.IsCancellationRequested)
            {
                testNumber++;

                // Take baseline snapshot
                var beforeSnapshot = TakeResourceSnapshot($"Test {testNumber} - Before");
                resourceHistory.Add(beforeSnapshot);

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Test {testNumber}: Sending {messagesPerBurst} messages...");

                // Send burst of messages
                var sendTasks = new List<Task>();
                for (var i = 0; i < messagesPerBurst; i++)
                {
                    var messageIndex = i;
                    sendTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await _channel.SendAsync(new TestDistributedActivity { Message = $"AutoTest-{testNumber}-{messageIndex}" }, testCancellation.Token);
                            _metrics.IncrementMessagesSent();
                        }
                        catch (Exception ex)
                        {
                            _metrics.IncrementSendErrors();
                            Console.WriteLine($"  [ERROR] Send failed: {ex.Message}");
                        }
                    }));
                }

                await Task.WhenAll(sendTasks);

                // Take after snapshot with delay to allow RabbitMQ resources to be cleaned up
                var afterSnapshot = await TakeDelayedResourceSnapshotAsync($"Test {testNumber} - After", 1500);
                resourceHistory.Add(afterSnapshot);

                // Analyze resource usage
                AnalyzeResourceUsage(beforeSnapshot, afterSnapshot, testNumber);

                // Wait for next test interval
                await Task.Delay(testInterval, testCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nTest cancelled by user.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nTest failed with error: {ex.Message}");
        }
        finally
        {
            testCancellation.Cancel();
            keyMonitorTask.Wait(1000); // Wait up to 1 second for key monitor to stop
        }

        // Final analysis
        Console.WriteLine("\n=== FINAL RESOURCE LEAK ANALYSIS ===");

        if (resourceHistory.Count >= 4) // At least 2 complete test cycles
        {
            var firstSnapshot = resourceHistory[0];
            var lastSnapshot = resourceHistory[resourceHistory.Count - 1];

            AnalyzeLongTermTrends(firstSnapshot, lastSnapshot, resourceHistory, testNumber);
            DetectResourceLeaks(resourceHistory);
        }
        else
        {
            Console.WriteLine("Insufficient data for leak analysis (test was too short).");
        }

        Console.WriteLine("\n=== TEST COMPLETE ===");
    }

    private static void AnalyzeResourceUsage(ResourceSnapshot before, ResourceSnapshot after, int testNumber)
    {
        var connectionDiff = after.Connections - before.Connections;
        var channelDiff = after.Channels - before.Channels;
        var memoryDiff = after.MemoryBytes - before.MemoryBytes;
        var gcDiff = (after.Gen0Collections + after.Gen1Collections + after.Gen2Collections) -
                    (before.Gen0Collections + before.Gen1Collections + before.Gen2Collections);

        Console.WriteLine($"  RabbitMQ Resource Changes:");
        Console.WriteLine($"    Connections: {before.Connections} → {after.Connections} ({connectionDiff:+0;-0;0})");
        Console.WriteLine($"    Channels: {before.Channels} → {after.Channels} ({channelDiff:+0;-0;0})");
        Console.WriteLine($"    App Memory: {before.MemoryBytes / 1024 / 1024:F1}MB → {after.MemoryBytes / 1024 / 1024:F1}MB ({memoryDiff / 1024 / 1024:+0.0;-0.0;0.0}MB)");
        Console.WriteLine($"    GC Collections: {gcDiff}");

        // Flag RabbitMQ-specific issues
        if (connectionDiff > 0)
            Console.WriteLine($"  🚨 RABBITMQ CONNECTION LEAK: {connectionDiff} connections not cleaned up!");
        if (channelDiff > 0)
            Console.WriteLine($"  🚨 RABBITMQ CHANNEL LEAK: {channelDiff} channels not cleaned up!");
            
        // Expected behavior: channels should return to baseline (1 receiver channel)
        if (after.Channels > 1)
            Console.WriteLine($"  ⚠️  CHANNELS REMAINING: {after.Channels} channels active (expected: 1 receiver channel)");
            
        // Expected behavior: connections should remain stable (typically 1)
        if (after.Connections > 1)
            Console.WriteLine($"  ⚠️  MULTIPLE CONNECTIONS: {after.Connections} connections active (typically should be 1)");

        if (memoryDiff > 10 * 1024 * 1024) // More than 10MB growth
            Console.WriteLine($"  ⚠️  MEMORY GROWTH: {memoryDiff / 1024 / 1024:F1}MB increase (may indicate resource accumulation)");

        Console.WriteLine();
    }

    private static void AnalyzeLongTermTrends(ResourceSnapshot first, ResourceSnapshot last, List<ResourceSnapshot> history, int totalTests)
    {
        var totalConnectionGrowth = last.Connections - first.Connections;
        var totalChannelGrowth = last.Channels - first.Channels;
        var totalMemoryGrowth = last.MemoryBytes - first.MemoryBytes;
        var testDuration = last.Timestamp - first.Timestamp;

        Console.WriteLine($"RabbitMQ Long-Term Analysis ({totalTests} test cycles over {testDuration:mm\\:ss}):");
        Console.WriteLine($"  Connection Growth: {first.Connections} → {last.Connections} (net: {totalConnectionGrowth:+0;-0;0})");
        Console.WriteLine($"  Channel Growth: {first.Channels} → {last.Channels} (net: {totalChannelGrowth:+0;-0;0})");
        Console.WriteLine($"  App Memory Growth: {totalMemoryGrowth / 1024 / 1024:F1}MB");
        Console.WriteLine($"  Messages Sent: {_metrics.MessagesSent}");
        Console.WriteLine($"  Send Errors: {_metrics.SendErrors}");

        // RabbitMQ-specific analysis
        Console.WriteLine($"\nRabbitMQ Resource Health Assessment:");
        
        // Connection analysis
        if (totalConnectionGrowth > 0)
        {
            Console.WriteLine($"  🚨 CONNECTION LEAK: Net increase of {totalConnectionGrowth} connections over test period");
        }
        else if (last.Connections == 1)
        {
            Console.WriteLine($"  ✅ CONNECTIONS: Stable at {last.Connections} (optimal)");
        }
        else if (last.Connections > 1)
        {
            Console.WriteLine($"  ⚠️  CONNECTIONS: {last.Connections} active (may be acceptable depending on usage)");
        }
        
        // Channel analysis  
        if (totalChannelGrowth > 0)
        {
            Console.WriteLine($"  🚨 CHANNEL LEAK: Net increase of {totalChannelGrowth} channels over test period");
        }
        else if (last.Channels == 1)
        {
            Console.WriteLine($"  ✅ CHANNELS: Stable at {last.Channels} (optimal - receiver channel only)");
        }
        else if (last.Channels > 1)
        {
            Console.WriteLine($"  ⚠️  CHANNELS: {last.Channels} active (may indicate incomplete cleanup)");
        }

        // Calculate growth rates for leak detection
        if (totalTests > 1)
        {
            var avgConnectionGrowthPerTest = (double)totalConnectionGrowth / totalTests;
            var avgChannelGrowthPerTest = (double)totalChannelGrowth / totalTests;

            if (avgConnectionGrowthPerTest > 0.1)
            {
                Console.WriteLine($"  🚨 CONNECTION LEAK RATE: {avgConnectionGrowthPerTest:F2} connections per test cycle");
            }
            if (avgChannelGrowthPerTest > 0.1)
            {
                Console.WriteLine($"  🚨 CHANNEL LEAK RATE: {avgChannelGrowthPerTest:F2} channels per test cycle");
            }
        }
        
        // Resource efficiency
        if (_metrics.MessagesSent > 0)
        {
            var messagesPerConnection = (double)_metrics.MessagesSent / Math.Max(1, last.Connections);
            var messagesPerChannelPeak = (double)_metrics.MessagesSent / Math.Max(1, history.Max(h => h.Channels));
            
            Console.WriteLine($"\nResource Efficiency:");
            Console.WriteLine($"  Messages per connection: {messagesPerConnection:F0}");
            Console.WriteLine($"  Messages per peak channel: {messagesPerChannelPeak:F0}");
        }
    }

    private static void DetectResourceLeaks(List<ResourceSnapshot> history)
    {
        Console.WriteLine("\n=== RABBITMQ RESOURCE LEAK DETECTION ===");
        Console.WriteLine("(Focused on RabbitMQMessageProvider resource usage, not overall app memory)");

        var leakDetected = false;

        // CRITICAL: Check for RabbitMQ connection leaks
        var connectionGrowthCount = 0;
        var maxConnections = 0;
        var minConnections = int.MaxValue;
        
        for (int i = 1; i < history.Count; i++)
        {
            if (history[i].Connections > history[i - 1].Connections)
                connectionGrowthCount++;
            
            maxConnections = Math.Max(maxConnections, history[i].Connections);
            minConnections = Math.Min(minConnections, history[i].Connections);
        }

        Console.WriteLine($"Connection Analysis: Min={minConnections}, Max={maxConnections}, Growth in {connectionGrowthCount}/{history.Count-1} measurements");

        if (connectionGrowthCount > history.Count * 0.5) // More than 50% of measurements show growth
        {
            Console.WriteLine("🚨 RABBITMQ CONNECTION LEAK DETECTED: Connections consistently growing over time");
            Console.WriteLine("   This indicates connections are not being properly closed in RabbitMQMessageProvider");
            leakDetected = true;
        }
        else if (maxConnections > 2) // Should typically have 1-2 connections max
        {
            Console.WriteLine($"⚠️  EXCESSIVE CONNECTIONS: Peak of {maxConnections} connections detected");
            Console.WriteLine("   Expected: 1-2 connections for normal operation");
            leakDetected = true;
        }

        // CRITICAL: Check for RabbitMQ channel leaks (most common issue)
        var channelGrowthCount = 0;
        var maxChannels = 0;
        var minChannels = int.MaxValue;
        var finalChannels = history[history.Count - 1].Channels;
        var expectedFinalChannels = 1; // Should be 1 receiver channel after test

        for (int i = 1; i < history.Count; i++)
        {
            if (history[i].Channels > history[i - 1].Channels)
                channelGrowthCount++;
                
            maxChannels = Math.Max(maxChannels, history[i].Channels);
            minChannels = Math.Min(minChannels, history[i].Channels);
        }

        Console.WriteLine($"Channel Analysis: Min={minChannels}, Max={maxChannels}, Final={finalChannels}, Growth in {channelGrowthCount}/{history.Count-1} measurements");

        if (finalChannels > expectedFinalChannels)
        {
            Console.WriteLine($"🚨 RABBITMQ CHANNEL LEAK DETECTED: {finalChannels} channels remain active after test completion");
            Console.WriteLine($"   Expected: {expectedFinalChannels} channel (receiver only)");
            Console.WriteLine("   This indicates send channels are not being properly disposed in InternalSendAsync");
            leakDetected = true;
        }
        else if (channelGrowthCount > history.Count * 0.7)
        {
            Console.WriteLine("🚨 RABBITMQ CHANNEL LEAK DETECTED: Channels consistently growing during test");
            Console.WriteLine("   This indicates temporary channels for sending are accumulating");
            leakDetected = true;
        }
        else if (maxChannels > finalChannels + 50) // Allow some temporary channels during load
        {
            Console.WriteLine($"⚠️  EXCESSIVE TEMPORARY CHANNELS: Peak of {maxChannels} channels during test");
            Console.WriteLine("   This may indicate slow channel disposal or high concurrency issues");
        }

        // Memory analysis (secondary - focus on patterns, not absolute values)
        var firstMemory = history[0].MemoryBytes;
        var lastMemory = history[history.Count - 1].MemoryBytes;
        var memoryGrowthPercent = ((double)lastMemory - firstMemory) / firstMemory * 100;

        // More conservative memory growth check since we can't isolate RabbitMQ memory
        if (memoryGrowthPercent > 100) // Only flag significant memory growth
        {
            Console.WriteLine($"⚠️  SIGNIFICANT MEMORY GROWTH: {memoryGrowthPercent:F1}% increase during test");
            Console.WriteLine("   This may indicate memory leaks, but could also be normal app behavior");
            Console.WriteLine("   Focus on connection/channel leaks as primary indicators");
        }

        // Final assessment
        if (!leakDetected)
        {
            Console.WriteLine("✅ No RabbitMQ resource leaks detected in this test run");
            Console.WriteLine("   RabbitMQMessageProvider appears to be managing connections/channels properly");
        }

        // Targeted recommendations
        Console.WriteLine("\n=== RABBITMQ-SPECIFIC RECOMMENDATIONS ===");
        if (leakDetected)
        {
            Console.WriteLine("🔧 IMMEDIATE ACTION REQUIRED IN RabbitMQMessageProvider:");
            Console.WriteLine("   - Ensure 'await using var channel = ...' pattern in InternalSendAsync");
            Console.WriteLine("   - Verify channel.CloseAsync() is called in all code paths");
            Console.WriteLine("   - Add try-finally blocks around channel operations");
            Console.WriteLine("   - Consider channel pooling for high-throughput scenarios");
            Console.WriteLine("   - Monitor _activeConnections and _activeChannels counters");
        }
        else
        {
            Console.WriteLine("✅ RabbitMQ resource management appears healthy, but monitor:");
            Console.WriteLine("   - Channel disposal patterns under higher loads");
            Console.WriteLine("   - Connection stability during network issues");
            Console.WriteLine("   - Resource usage during extended operations");
        }
    }

    private static ResourceSnapshot TakeResourceSnapshot(string label)
    {
        // Force garbage collection to get accurate memory reading
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var connections = 0;
        var channels = 0;

        try
        {
            if (_channel is SenseNet.Messaging.RabbitMQ.RabbitMQMessageProvider provider)
            {
                (connections, channels) = provider.GetPoolStatus();
            }
        }
        catch
        {
            // Ignore errors getting pool status - provider may not be available
        }

        return new ResourceSnapshot
        {
            Label = label,
            Timestamp = DateTime.Now,
            Connections = connections,
            Channels = channels,
            MemoryBytes = GC.GetTotalMemory(false),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };
    }

    /// <summary>
    /// Takes a resource snapshot with additional wait time to allow for RabbitMQ resource cleanup
    /// </summary>
    private static async Task<ResourceSnapshot> TakeDelayedResourceSnapshotAsync(string label, int delayMs = 1000)
    {
        // Allow time for async disposal operations to complete
        await Task.Delay(delayMs);
        
        // Force aggressive garbage collection to ensure all finalizers run
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(100); // Small delay between GC calls
        }
        GC.Collect();

        var connections = 0;
        var channels = 0;

        try
        {
            if (_channel is SenseNet.Messaging.RabbitMQ.RabbitMQMessageProvider provider)
            {
                (connections, channels) = provider.GetPoolStatus();
            }
        }
        catch
        {
            // Ignore errors getting pool status - provider may not be available
        }

        return new ResourceSnapshot
        {
            Label = label,
            Timestamp = DateTime.Now,
            Connections = connections,
            Channels = channels,
            MemoryBytes = GC.GetTotalMemory(false),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };
    }

    private static async Task RunStressTest(int messageCount)
    {
        Console.WriteLine($"Starting stress test with {messageCount} messages...");
        Console.WriteLine("Monitor resource usage (Task Manager/htop) during this test!");

        var beforeMemory = GC.GetTotalMemory(false);
        var stopwatch = Stopwatch.StartNew();

        // Send messages as fast as possible
        var tasks = new List<Task>();
        for (var i = 0; i < messageCount; i++)
        {
            var messageIndex = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await _channel.SendAsync(new TestDistributedActivity { Message = $"Stress test message {messageIndex}" }, default);
                    _metrics.IncrementMessagesSent();
                }
                catch (Exception)
                {
                    _metrics.IncrementSendErrors();
                    // Error details are already logged by the provider
                }
            }));
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // Force garbage collection to see actual memory usage
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var afterMemory = GC.GetTotalMemory(false);
        var memoryIncrease = afterMemory - beforeMemory;

        Console.WriteLine($"Stress test completed:");
        Console.WriteLine($"  Time: {stopwatch.ElapsedMilliseconds}ms");
        Console.WriteLine($"  Rate: {(double)messageCount / stopwatch.ElapsedMilliseconds * 1000:F1} msg/sec");
        Console.WriteLine($"  Memory increase: {memoryIncrease / 1024 / 1024:F2} MB");
        Console.WriteLine($"  Errors: {_metrics.SendErrors}");
    }

    private static void SimulateCrash()
    {
        Console.WriteLine("Simulating application crash during message processing...");
        Console.WriteLine("Send a message from another instance, then press any key to 'crash' this instance");
        Console.ReadKey();

        // Simulate crash by forcefully terminating without proper cleanup
        Console.WriteLine("CRASH! (Terminating without proper cleanup)");
        Environment.Exit(1);
    }

    private static void SimulateDisconnect()
    {
        Console.WriteLine("Network disconnect simulation:");
        Console.WriteLine("1. Disconnect your network or stop RabbitMQ service");
        Console.WriteLine("2. Try sending messages (should fail)");
        Console.WriteLine("3. Reconnect network/restart RabbitMQ");
        Console.WriteLine("4. Try sending messages again");
        Console.WriteLine("This tests connection resilience and auto-reconnection");
    }

    private static async Task ShowRabbitMQResourceStatus()
    {
        Console.WriteLine("=== RABBITMQ RESOURCE STATUS ===");
        
        // Take a comprehensive snapshot
        var snapshot = await TakeDelayedResourceSnapshotAsync("Current Status", 500);
        
        Console.WriteLine($"Timestamp: {snapshot.Timestamp:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"RabbitMQ Connections: {snapshot.Connections}");
        Console.WriteLine($"RabbitMQ Channels: {snapshot.Channels}");
        Console.WriteLine($"Application Memory: {snapshot.MemoryBytes / 1024 / 1024:F2} MB");
        Console.WriteLine($"GC Collections: Gen0={snapshot.Gen0Collections}, Gen1={snapshot.Gen1Collections}, Gen2={snapshot.Gen2Collections}");
        
        // Health assessment
        Console.WriteLine("\nHealth Assessment:");
        if (snapshot.Connections == 1)
            Console.WriteLine("✅ Connections: Optimal (1 connection)");
        else if (snapshot.Connections == 0)
            Console.WriteLine("⚠️  Connections: None active (may indicate connection issue)");
        else
            Console.WriteLine($"⚠️  Connections: {snapshot.Connections} active (review if excessive)");
            
        if (snapshot.Channels == 1)
            Console.WriteLine("✅ Channels: Optimal (1 receiver channel)");
        else if (snapshot.Channels == 0)
            Console.WriteLine("⚠️  Channels: None active (may indicate initialization issue)");
        else
            Console.WriteLine($"⚠️  Channels: {snapshot.Channels} active (may indicate resource leak or high load)");
            
        ShowBasicMetrics();
    }

    private static void ShowStatus()
    {
        Console.WriteLine($"Active Instance ID: {_channel.ClusterMemberInfo.InstanceID}");

        // Try to get pool status if the method is available
        try
        {
            if (_channel is SenseNet.Messaging.RabbitMQ.RabbitMQMessageProvider provider)
            {
                var (connections, channels) = provider.GetPoolStatus();
                Console.WriteLine($"Active Connections: {connections}");
                Console.WriteLine($"Active Channels: {channels}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not get pool status: {ex.Message}");
        }

        ShowBasicMetrics();
    }

    private static void ShowBasicMetrics()
    {
        Console.WriteLine($"Messages Sent: {_metrics.MessagesSent}");
        Console.WriteLine($"Messages Received: {_metrics.MessagesReceived}");
        Console.WriteLine($"Messages Processed: {_metrics.MessagesProcessed}");
        Console.WriteLine($"Send Errors: {_metrics.SendErrors}");
        Console.WriteLine($"Processing Errors: {_metrics.MessageErrors}");
    }

    private static void ShowDetailedMetrics()
    {
        Console.WriteLine("=== DETAILED METRICS ===");
        ShowBasicMetrics();

        if (_metrics.SendTimes.Count > 0)
        {
            var avgSendTime = _metrics.SendTimes.Sum() / _metrics.SendTimes.Count;
            var maxSendTime = _metrics.SendTimes.Max();
            Console.WriteLine($"Average Send Time: {avgSendTime:F2}ms");
            Console.WriteLine($"Max Send Time: {maxSendTime}ms");
        }

        if (_metrics.ProcessingTimes.Count > 0)
        {
            var avgProcessTime = _metrics.ProcessingTimes.Sum() / _metrics.ProcessingTimes.Count;
            var maxProcessTime = _metrics.ProcessingTimes.Max();
            Console.WriteLine($"Average Processing Time: {avgProcessTime:F2}ms");
            Console.WriteLine($"Max Processing Time: {maxProcessTime}ms");
        }

        // Calculate message loss rate
        var totalSent = _metrics.MessagesSent;
        var totalReceived = _metrics.MessagesReceived;
        if (totalSent > 0)
        {
            var lossRate = (double)(totalSent - totalReceived) / totalSent * 100;
            Console.WriteLine($"Potential Message Loss Rate: {lossRate:F2}%");
        }

        Console.WriteLine($"Current Memory Usage: {GC.GetTotalMemory(false) / 1024 / 1024:F2} MB");
    }

    private static void PrintMetrics(object? state)
    {
        if (_metrics.MessagesSent > 0 || _metrics.MessagesReceived > 0)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sent: {_metrics.MessagesSent}, Received: {_metrics.MessagesReceived}, Errors: {_metrics.SendErrors + _metrics.MessageErrors}");
        }
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

public class ResourceSnapshot
{
    public required string Label { get; set; }
    public DateTime Timestamp { get; set; }
    public int Connections { get; set; }
    public int Channels { get; set; }
    public long MemoryBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}

public class TestMetrics
{
    private long _messagesSent;
    private long _messagesReceived;
    private long _messagesProcessed;
    private long _sendErrors;
    private long _messageErrors;

    public readonly ConcurrentBag<long> SendTimes = new ConcurrentBag<long>();
    public readonly ConcurrentBag<long> ProcessingTimes = new ConcurrentBag<long>();

    public long MessagesSent => _messagesSent;
    public long MessagesReceived => _messagesReceived;
    public long MessagesProcessed => _messagesProcessed;
    public long SendErrors => _sendErrors;
    public long MessageErrors => _messageErrors;

    public void IncrementMessagesSent() => Interlocked.Increment(ref _messagesSent);
    public void IncrementMessagesReceived() => Interlocked.Increment(ref _messagesReceived);
    public void IncrementMessagesProcessed() => Interlocked.Increment(ref _messagesProcessed);
    public void IncrementSendErrors() => Interlocked.Increment(ref _sendErrors);
    public void IncrementMessageErrors() => Interlocked.Increment(ref _messageErrors);

    public void RecordSendTime(long milliseconds) => SendTimes.Add(milliseconds);
    public void RecordProcessingTime(long milliseconds) => ProcessingTimes.Add(milliseconds);
}