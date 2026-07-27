using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreqScene.Kiosk;

internal sealed class MainThreadDispatcher(ILogger? logger = null) : IUiDispatcher
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

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
                    _logger.LogError(ex, "posted action failed");
                }
            }

            idle?.Invoke();
        }

        _queue.CompleteAdding();
    }
}
