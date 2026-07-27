using FreqScene;
using FreqScene.Controller;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;

LogLevel? cliLevel = null;
for (var i = 0; i + 1 < args.Length; i++)
{
    if (args[i] == "--log-level")
    {
        cliLevel = FreqSceneLogging.TryParseLevel(args[i + 1]);
    }
}

using var loggerFactory = FreqSceneLogging.Create(
    "freqscene-controller", cliLevel ?? LogLevel.Information, console: false);

var settings = SettingsStore.Load();

IApplication app = Application.Create().Init();
VisualizerCoordinator? coordinator = null;
RemoteServerManager? remoteManager = null;
try
{
    coordinator = new VisualizerCoordinator { UiDispatcher = new ControllerDispatcher(app) };

    coordinator.SetStopped(true);

    remoteManager = new RemoteServerManager(coordinator, settings, loggerFactory);
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
