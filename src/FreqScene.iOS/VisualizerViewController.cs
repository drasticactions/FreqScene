using ProjectMDotNet;
using UIKit;

namespace FreqScene.iOS;

public sealed class VisualizerViewController : UIViewController
{
    private ProjectMGLView? _glView;
    private SyntheticAudioSource? _audio;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.Black;

        _glView = new ProjectMGLView(View.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
        };
        View.AddSubview(_glView);
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        if (_audio is null && _glView is { } glView)
        {
            _audio = new SyntheticAudioSource(chunk => glView.AddPcm(chunk, AudioChannels.Stereo));
            _audio.Start();
        }
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        _audio?.Dispose();
        _audio = null;
    }

#if IOS
    public override bool PrefersStatusBarHidden() => true;
#endif
}
