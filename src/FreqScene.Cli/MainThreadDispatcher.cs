using System.Collections.Concurrent;

namespace FreqScene.Cli;

internal sealed class MainThreadDispatcher : IUiDispatcher
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _mainThreadId;

    public void Post(Action action)
    {
        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // The loop is shutting down; late posts are dropped.
        }
    }

    public void Run(CancellationToken token, Action? idle = null)
    {
        while (!token.IsCancellationRequested)
        {
            if (_queue.TryTake(out var action, 200))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"error: {ex.Message}");
                }
            }

            idle?.Invoke();
        }

        _queue.CompleteAdding();
    }
}
