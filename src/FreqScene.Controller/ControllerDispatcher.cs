using Terminal.Gui.App;

namespace FreqScene.Controller;

public sealed class ControllerDispatcher(IApplication app) : IUiDispatcher
{
    public bool CheckAccess() => app.MainThreadId == Environment.CurrentManagedThreadId;

    public void Post(Action action) => app.Invoke(action);
}
