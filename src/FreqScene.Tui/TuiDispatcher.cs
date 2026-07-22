using Terminal.Gui.App;

namespace FreqScene.Tui;

public sealed class TuiDispatcher(IApplication app) : IUiDispatcher
{
    public bool CheckAccess() => app.MainThreadId == Environment.CurrentManagedThreadId;

    public void Post(Action action) => app.Invoke(action);
}
