using FreqScene;
using FreqScene.Tui;
using Terminal.Gui.App;

var settings = SettingsStore.Load();

IApplication app = Application.Create().Init();
VisualizerCoordinator? coordinator = null;
RemoteServerManager? remoteManager = null;
try
{
    coordinator = new VisualizerCoordinator { UiDispatcher = new TuiDispatcher(app) };

    coordinator.SetStopped(true);

    remoteManager = new RemoteServerManager(coordinator, settings);
    using MainView mainView = new(app, coordinator, remoteManager, settings);

    if (settings.AllowRemoteConnections)
    {
        _ = remoteManager.ApplyAsync();
    }

    app.Run(mainView);
}
finally
{
    remoteManager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
    coordinator?.Dispose();
    app.Dispose();
}
