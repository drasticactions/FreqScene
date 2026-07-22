using UIKit;

namespace FreqScene.iOS;

internal static class Application
{
    private static void Main(string[] args) =>
        UIApplication.Main(args, principalClass: null, delegateClass: typeof(AppDelegate));
}
