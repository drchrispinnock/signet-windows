namespace Signet.Services;

/// <summary>
/// Device state spans multiple APDUs and JavaScript contexts. A process-wide lease protects the
/// entire session; individual HID exchange ordering is not sufficient. (Here the second context is
/// the dApp manager's own bridge, which can drive the same Ledger as the main one.)
/// </summary>
public sealed class LedgerSessions
{
    public static LedgerSessions Shared { get; } = new();

    public sealed class TooManyWaiting() : Exception("Too many requests are waiting for the Ledger.");

    private readonly Lock gate = new();
    private readonly Dictionary<string, Guid> owners = [];
    private readonly Dictionary<string, LinkedList<(Guid Id, TaskCompletionSource<Guid> Ready)>> waiting = [];

    /// <summary>Waits until <c>device</c> is free and returns the lease to give back to <c>Release</c>.</summary>
    public Task<Guid> Acquire(string device, CancellationToken cancel = default)
    {
        var id = Guid.NewGuid();
        lock (gate)
        {
            cancel.ThrowIfCancellationRequested();
            if (!owners.ContainsKey(device))
            {
                owners[device] = id;
                return Task.FromResult(id);
            }
            if (!waiting.TryGetValue(device, out var list)) waiting[device] = list = new();
            if (list.Count >= 16) throw new TooManyWaiting();
            var ready = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            var node = list.AddLast((id, ready));
            if (cancel.CanBeCanceled)
                cancel.Register(() =>
                {
                    lock (gate)
                    {
                        if (node.List is null) return;   // already handed the device
                        list.Remove(node);
                    }
                    ready.TrySetCanceled(cancel);
                });
            return ready.Task;
        }
    }

    public void Release(Guid id, string device)
    {
        TaskCompletionSource<Guid>? next = null;
        Guid nextID = default;
        lock (gate)
        {
            if (!owners.TryGetValue(device, out var owner) || owner != id) return;
            if (waiting.TryGetValue(device, out var list) && list.First is { } first)
            {
                list.RemoveFirst();
                (nextID, next) = first.Value;
                owners[device] = nextID;
            }
            else
            {
                owners.Remove(device);
                waiting.Remove(device);
            }
        }
        if (next is not null && !next.TrySetResult(nextID)) Release(nextID, device);
    }
}
