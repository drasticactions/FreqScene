namespace FreqScene;

public interface IUiDispatcher
{
    bool CheckAccess();

    void Post(Action action);
}

public sealed class InlineUiDispatcher : IUiDispatcher
{
    public static InlineUiDispatcher Instance { get; } = new();

    public bool CheckAccess() => true;

    public void Post(Action action) => action();
}
