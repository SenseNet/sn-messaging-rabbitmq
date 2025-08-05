# RabbitMQ Implementation Resource Leak Testing

## Overview
This enhanced test application provides **RabbitMQ-specific resource leak detection** for the SenseNet RabbitMQ messaging provider. The testing focuses specifically on connection and channel resource management within the `RabbitMQMessageProvider` rather than general application memory usage.

## Key Improvements

### 🎯 **Focused Resource Monitoring**
- **Before**: Monitored total application memory (including tester overhead)  
- **After**: Specifically tracks RabbitMQ connections and channels using `GetPoolStatus()`
- **Benefit**: Accurate detection of `RabbitMQMessageProvider` resource leaks

### 🔍 **Enhanced Leak Detection**
- **Connection Monitoring**: Expects 1 stable connection for normal operation
- **Channel Monitoring**: Expects 1 receiver channel after test completion
- **Temporary Channel Tracking**: Monitors send channel disposal patterns
- **Delayed Snapshots**: Allows time for async disposal operations to complete

### 📊 **RabbitMQ-Specific Analysis**
- **Resource Health Assessment**: Clear indicators of optimal vs problematic states
- **Leak Rate Calculation**: Measures resource growth per test cycle
- **Efficiency Metrics**: Messages per connection/channel ratios
- **Targeted Recommendations**: Specific guidance for `RabbitMQMessageProvider` issues

## Test Environment Setup

### Prerequisites
1. Run the enhanced test application on at least 2 instances
2. Have RabbitMQ server running
3. Monitor system resources (Task Manager/htop/Activity Monitor)
4. Have ability to stop/start RabbitMQ service

## Available Commands

### 🔧 **Resource Monitoring Commands**
- `status` - Basic connection pool status and metrics
- `rmqstatus` - **NEW**: Detailed RabbitMQ resource health assessment
- `metrics` - Comprehensive message and performance metrics

### 🧪 **Testing Commands**  
- `autotest` - **ENHANCED**: Automated RabbitMQ resource leak detection (2 minutes)
- `stress <count>` - High-volume message sending for resource testing
- `<message> <count>` - Send multiple messages for load testing

### 🚨 **Simulation Commands**
- `crash` - Simulate application crash during message processing
- `disconnect` - Simulate network disconnect scenarios
- `exit` - Quit the application

---

## Test 1: Enhanced Automated Resource Leak Detection

### Objective
Automatically detect RabbitMQ-specific resource leaks in the `RabbitMQMessageProvider` implementation.

### Steps (Fully Automated)
1. Start one instance of the enhanced test app
2. Execute: `autotest` command  
3. **The test runs automatically for 2 minutes** - monitors RabbitMQ resources specifically
4. Watch real-time RabbitMQ resource analysis
5. Review final leak detection report focused on connection/channel management

### What the Enhanced Test Does
- **RabbitMQ-Focused Monitoring**: Tracks connections and channels specifically
- **Delayed Resource Snapshots**: Allows time for async channel disposal
- **Smart Leak Detection**: Uses RabbitMQ-appropriate thresholds
- **Resource Health Assessment**: Clear optimal vs problematic states
- **Targeted Analysis**: Focuses on `RabbitMQMessageProvider` behavior

### Expected Results with Enhanced Monitoring
```
[14:23:10] Test 1: Sending 100 messages...
  RabbitMQ Resource Changes:
    Connections: 1 → 1 (0)
    Channels: 1 → 3 (+2)
    App Memory: 12.3MB → 15.1MB (+2.8MB)
  ⚠️  CHANNELS REMAINING: 3 channels active (expected: 1 receiver channel)

=== RABBITMQ RESOURCE LEAK DETECTION ===
(Focused on RabbitMQMessageProvider resource usage, not overall app memory)

🚨 RABBITMQ CHANNEL LEAK DETECTED: 5 channels remain active after test completion
   Expected: 1 channel (receiver only)
   This indicates send channels are not being properly disposed in InternalSendAsync

=== RABBITMQ-SPECIFIC RECOMMENDATIONS ===
� IMMEDIATE ACTION REQUIRED IN RabbitMQMessageProvider:
   - Ensure 'await using var channel = ...' pattern in InternalSendAsync
   - Verify channel.CloseAsync() is called in all code paths
   - Add try-finally blocks around channel operations
```

### Enhanced Measurements Automatically Collected
- **Connection Analysis**: Min/Max/Final connection counts with health assessment
- **Channel Analysis**: Growth patterns, peak usage, final cleanup status  
- **Resource Efficiency**: Messages per connection/channel ratios
- **Leak Rate Detection**: Growth per test cycle calculation
- **Health Status**: Clear optimal vs problematic resource states

### Proof This Shows with Enhanced Monitoring
- **RabbitMQ Resource Management**: Direct measurement of provider-specific leaks
- **Channel Disposal Issues**: Precise detection of incomplete cleanup
- **Connection Stability**: Monitoring of connection pool health  
- **Production Readiness**: Evidence of resource management quality

### Why Enhanced RabbitMQ Monitoring Is Better
- ✅ **Provider-Specific**: Focuses on actual `RabbitMQMessageProvider` issues
- ✅ **Accurate Detection**: Not confused by tester application memory usage
- ✅ **Async-Aware**: Accounts for delayed disposal operations
- ✅ **Targeted Recommendations**: Specific to RabbitMQ implementation patterns
- ✅ **Real Thresholds**: Uses realistic expectations (1 connection, 1 receiver channel)

### New RabbitMQ Status Command
Use `rmqstatus` for detailed real-time analysis:
```
=== RABBITMQ RESOURCE STATUS ===
Timestamp: 2025-08-04 14:23:45
RabbitMQ Connections: 1
RabbitMQ Channels: 1
Application Memory: 45.23 MB
GC Collections: Gen0=12, Gen1=3, Gen2=1

Health Assessment:
✅ Connections: Optimal (1 connection)
✅ Channels: Optimal (1 receiver channel)
```

---

## Test 2: Connection Failure Recovery

### Objective
Demonstrate lack of automatic reconnection when RabbitMQ goes down.

### Steps
1. Start two instances of the test app
2. Verify they can communicate (send test messages)
3. Stop RabbitMQ service/container
4. Try sending messages from both instances
5. Restart RabbitMQ service
6. Try sending messages again (without restarting apps)

### Expected Results with Current Code
- Step 4: Send operations fail (recorded in metrics)
- Step 6: Send operations continue to fail
- Applications don't automatically reconnect
- Manual restart required to restore functionality

### Measurements to Collect
- Send error count during outage
- Time to detect connection failure
- Whether messages resume after RabbitMQ restart (they shouldn't)
- Manual intervention required (yes)

### Proof This Shows
- **No Automatic Recovery**: System doesn't self-heal
- **Poor Resilience**: Single point of failure brings down messaging
- **Operational Overhead**: Requires manual intervention

---

## Test 3: Message Loss During Crashes

### Objective
Prove that auto-acknowledgment can cause message loss.

### Steps
1. Start two instances: Instance A and Instance B
2. From Instance A, send: `test message crash`
3. When Instance B shows "Received: test message crash", immediately run: `crash` command
4. This simulates crash during the 10-second processing delay
5. Restart Instance B
6. Check if the message was processed or lost

### Expected Results with Current Code
- Message is lost because it was auto-acknowledged
- No retry mechanism
- Instance B never completes processing the message

### Measurements to Collect
- Messages that were "received" but never "processed"
- Difference between received and processed counts
- Recovery behavior after restart

### Proof This Shows
- **Message Loss Risk**: Auto-acknowledge without proper processing
- **No Durability**: Crashed processing isn't retried
- **Data Integrity Issue**: Messages disappear silently

---

## Test 4: Performance Under Concurrent Load

### Objective
Measure performance degradation and resource usage patterns under sustained load.

### Steps (Multi-Instance Required)
1. **Start TWO instances** (Instance A and Instance B)
2. Verify communication: Send simple test message between instances
3. From Instance A, run baseline: Send 10 messages normally, note average send time
4. Execute: `stress 100` and note performance metrics on both instances
5. Execute: `stress 500` and note performance metrics on both instances  
6. Execute: `stress 1000` and note performance metrics on both instances
7. Compare send times and error rates across load levels
8. **Optional**: Run `autotest` on Instance A while monitoring Instance B

### Expected Results with Current Code
- Send times increase significantly with load
- Error rates may increase (channel exhaustion)
- Memory usage grows substantially on both instances
- Performance doesn't return to baseline quickly
- Instance B shows high receive load matching Instance A's send load

### Measurements to Collect
- Average send time at each load level (Instance A)
- Average processing time at each load level (Instance B)
- Error rates at each load level (both instances)
- Memory usage growth (both instances)
- Time to return to baseline performance
- **Sent vs Received ratio**: Should be close to 1:1 across instances

### Proof This Shows
- **Poor Scalability**: Performance degrades non-linearly
- **Resource Inefficiency**: Each message creates new channel
- **Memory Management Issues**: Growing resource usage
- **Cross-Instance Impact**: Load on one instance affects others

---

## Test 5: Network Partition Behavior

### Objective
Test behavior during network connectivity issues.

### Steps
1. Start two instances on different machines (or simulate with firewall rules)
2. Establish communication between instances
3. Block network connectivity between app and RabbitMQ
4. Try sending messages (should fail)
5. Restore connectivity
6. Check if messaging resumes automatically

### Expected Results with Current Code
- Messages fail during network partition
- No automatic retry when network returns
- Applications remain disconnected until restart

### Measurements to Collect
- Time to detect network failure
- Error count during partition
- Whether recovery happens automatically (it shouldn't)
- Time to manual recovery

### Proof This Shows
- **Network Resilience Gap**: No handling of temporary network issues
- **Poor Fault Tolerance**: Cannot recover from transient failures

---

## Comparative Analysis

### Current Implementation Issues Demonstrated

| Issue | Test | Measurable Impact |
|-------|------|------------------|
| Resource Leaks | Test 1 | Growing memory/connections |
| No Auto-Reconnection | Test 2 | Manual intervention required |
| Message Loss | Test 3 | Lost messages during crashes |
| Poor Scalability | Test 4 | Degraded performance under load |
| Network Intolerance | Test 5 | No recovery from network issues |

### Success Criteria for Improved Implementation

An improved version should show:
- **Test 1**: Stable resource usage, quick return to baseline
- **Test 2**: Automatic reconnection within reasonable time
- **Test 3**: Message retry after crash (with manual acknowledgment)
- **Test 4**: Linear performance scaling, stable error rates
- **Test 5**: Automatic recovery when network returns

---

## How to Run the Tests

### Quick Test Sequence (Multi-Instance)
```bash
# Terminal 1 (Instance A)
dotnet run
# Wait for startup, note InstanceID

# Terminal 2 (Instance B)  
dotnet run
# Wait for startup, note different InstanceID

# In Instance A:
hello test          # Should appear in Instance B - verify communication
rmqstatus          # Check baseline RabbitMQ resource status
autotest           # Enhanced RabbitMQ resource leak test (2 minutes)
rmqstatus          # Check final RabbitMQ resource status

# In Instance B:
metrics            # Check receive metrics during/after Instance A's autotest
rmqstatus          # Compare RabbitMQ status with Instance A
```

### Manual Testing Sequence (if needed)
```bash
# Terminal 1
dotnet run

# Terminal 2  
dotnet run

# In Terminal 1:
rmqstatus        # Enhanced RabbitMQ baseline
stress 500       # Load test (will be received by Terminal 2)
rmqstatus        # Check RabbitMQ resources after load
disconnect       # Follow instructions for network testing
crash           # Follow instructions for message loss testing
```

### Single-Instance Testing (Limited)
```bash
# Terminal 1
dotnet run

# Commands available:
autotest         # Enhanced RabbitMQ resource leak detection
stress 1000      # Load testing (may hit channel limits)
rmqstatus        # Detailed RabbitMQ resource monitoring
status           # Basic resource monitoring
metrics          # Detailed analysis
```

**⚠️ Note**: Single-instance testing won't show message processing metrics since instances ignore their own messages. Use multi-instance setup for complete testing.

---

## Expected Outcomes with Enhanced Monitoring

These tests will provide concrete, measurable evidence of:

### 1. **RabbitMQ Resource Management Problems** 
- **Targeted Detection**: `autotest` provides definitive proof of RabbitMQ-specific issues
- **Channel Leak Evidence**: Precise detection of undisposed send channels
- **Connection Monitoring**: Tracking of connection pool stability
- **Resource Recovery Analysis**: Whether resources return to baseline after load

### 2. **Enhanced Analysis Capabilities**
- **Provider-Specific Metrics**: Direct monitoring of `RabbitMQMessageProvider` behavior
- **Realistic Thresholds**: Expectations based on RabbitMQ best practices
- **Async-Aware Testing**: Accounts for delayed disposal operations
- **Health Assessment**: Clear indicators of optimal vs problematic states

### 3. **Improved Testing Efficiency** 
- **Focused Results**: No confusion from general application memory usage
- **Faster Detection**: RabbitMQ-specific thresholds catch issues sooner
- **Better Recommendations**: Targeted advice for `RabbitMQMessageProvider` improvements
- **Production-Ready Insights**: Relevant metrics for production monitoring

### Enhanced vs Previous Testing Benefits

| Aspect | Previous Testing | Enhanced RabbitMQ Testing |
|--------|-----------------|---------------------------|
| **Focus** | General app memory | RabbitMQ connections/channels |
| **Accuracy** | Includes tester overhead | Pure provider resource usage |
| **Thresholds** | Generic percentages | RabbitMQ-specific expectations |
| **Detection Speed** | Slow (high thresholds) | Fast (precise monitoring) |
| **Recommendations** | Generic advice | `RabbitMQMessageProvider`-specific |
| **Production Value** | Limited applicability | Direct production insights |

### Key Monitoring Improvements

#### **Connection Monitoring**
- **Expected**: 1 stable connection for normal operation
- **Detection**: Flags multiple connections or connection growth
- **Analysis**: Connection stability during load and recovery

#### **Channel Monitoring** (Most Critical)
- **Expected**: 1 receiver channel after operations complete
- **Detection**: Tracks temporary send channel disposal
- **Analysis**: Channel accumulation patterns and cleanup efficiency

#### **Resource Health Assessment**
- **Real-time Status**: `rmqstatus` command for immediate analysis
- **Trend Analysis**: Growth patterns over multiple test cycles
- **Efficiency Metrics**: Messages per connection/channel ratios

This enhanced monitoring provides the precise, actionable data needed to improve the `RabbitMQMessageProvider` implementation and ensure production readiness.