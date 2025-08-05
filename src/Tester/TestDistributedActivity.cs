using SenseNet.Communication.Messaging;

namespace Tester;

public class TestDistributedActivity : DistributedAction
{
    public required string Message { get; set; }
    public override string TraceMessage => string.Empty;
    public override Task DoActionAsync(bool onRemote, bool isFromMe, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}