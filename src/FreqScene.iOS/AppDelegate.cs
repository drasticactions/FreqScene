using Foundation;
using UIKit;

namespace FreqScene.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = new ServerBrowserViewController(),
        };
        Window.MakeKeyAndVisible();
        return true;
    }
}
