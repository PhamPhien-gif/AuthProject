using System.Collections.Concurrent;

namespace AuthProject.TestSupport;

internal sealed record TestEmail(string Recipient, string Subject, string Body);

internal sealed class FakeEmailTransport
{
    private readonly ConcurrentQueue<TestEmail> sent = new();
    public IReadOnlyCollection<TestEmail> Sent => sent.ToArray();
    public Exception? Failure { get; set; }
    public Task SendAsync(TestEmail message, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        if (Failure is not null)
            return Task.FromException(Failure);

        sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

