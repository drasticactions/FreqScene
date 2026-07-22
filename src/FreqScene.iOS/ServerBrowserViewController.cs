using Foundation;
using FreqScene.Remote;
using UIKit;

namespace FreqScene.iOS;

public sealed class ServerBrowserViewController : UIViewController, IUITableViewDataSource, IUITableViewDelegate
{
    private const string LastServerKey = "FreqScene.LastServerName";
    private const string CellId = "server";

    private readonly List<NSNetService> _services = [];
    private NSNetServiceBrowser? _browser;
    private UITableView? _table;
    private bool _autoJoinArmed = true;
    private bool _joining;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.Black;

        var title = new UILabel
        {
            Text = "FreqScene",
            TextColor = UIColor.White,
            Font = UIFont.BoldSystemFontOfSize(34),
            TextAlignment = UITextAlignment.Center,
            Frame = new CoreGraphics.CGRect(0, 60, View.Bounds.Width, 44),
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth,
        };
        View.AddSubview(title);

        _table = new UITableView(
            new CoreGraphics.CGRect(0, 120, View.Bounds.Width, View.Bounds.Height - 120),
            UITableViewStyle.Plain)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
            BackgroundColor = UIColor.Clear,
            DataSource = this,
            Delegate = this,
        };
        View.AddSubview(_table);
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        _joining = false;
        StartBrowsing();
    }

    public override void ViewDidDisappear(bool animated)
    {
        base.ViewDidDisappear(animated);
        StopBrowsing();
    }

    private void StartBrowsing()
    {
        if (_browser is not null)
        {
            return;
        }

        _browser = new NSNetServiceBrowser();
        _browser.FoundService += OnFoundService;
        _browser.ServiceRemoved += OnServiceRemoved;
        _browser.SearchForServices(RemoteProtocol.BonjourServiceType, "local.");
    }

    private void StopBrowsing()
    {
        if (_browser is { } browser)
        {
            _browser = null;
            browser.Stop();
            browser.FoundService -= OnFoundService;
            browser.ServiceRemoved -= OnServiceRemoved;
            browser.Dispose();
        }

        _services.Clear();
        _table?.ReloadData();
    }

    private void OnFoundService(object? sender, NSNetServiceEventArgs e)
    {
        if (_services.All(s => s.Name != e.Service.Name))
        {
            _services.Add(e.Service);
        }

        _table?.ReloadData();
        TryAutoJoin();
    }

    private void OnServiceRemoved(object? sender, NSNetServiceEventArgs e)
    {
        _services.RemoveAll(s => s.Name == e.Service.Name);
        _table?.ReloadData();
    }

    private async void TryAutoJoin()
    {
        // Auto-join only when exactly one server is visible and it is the one last joined.
        if (!_autoJoinArmed
            || _services.Count != 1
            || NSUserDefaults.StandardUserDefaults.StringForKey(LastServerKey) != _services[0].Name)
        {
            return;
        }

        _autoJoinArmed = false;
        var candidate = _services[0];
        await Task.Delay(1500);
        if (_browser is not null && !_joining && _services.Count == 1 && _services[0] == candidate)
        {
            await JoinAsync(candidate);
        }
    }

    private async Task JoinAsync(NSNetService service)
    {
        if (_joining)
        {
            return;
        }

        _joining = true;
        var address = await BonjourResolver.ResolveAsync(service);
        if (address is null)
        {
            _joining = false;
            return;
        }

        NSUserDefaults.StandardUserDefaults.SetString(service.Name, LastServerKey);
        PresentViewController(new VisualizerViewController(address, service.Name), animated: true, null);
    }

    public nint RowsInSection(UITableView tableView, nint section) => _services.Count + 1;

    public UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var cell = tableView.DequeueReusableCell(CellId) ?? new UITableViewCell(UITableViewCellStyle.Subtitle, CellId);
        if (indexPath.Row < _services.Count)
        {
            cell.TextLabel!.Text = _services[indexPath.Row].Name;
            if (cell.DetailTextLabel is { } detail)
            {
                detail.Text = "FreqScene server";
            }
        }
        else
        {
            cell.TextLabel!.Text = "Standalone Mode";
            if (cell.DetailTextLabel is { } detail)
            {
                detail.Text = "Visualize without a server";
            }
        }

        return cell;
    }

    [Export("tableView:didSelectRowAtIndexPath:")]
    public async void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, animated: true);
        _autoJoinArmed = false;
        if (indexPath.Row < _services.Count)
        {
            await JoinAsync(_services[indexPath.Row]);
        }
        else
        {
            PresentViewController(
                new VisualizerViewController { ModalPresentationStyle = UIModalPresentationStyle.FullScreen },
                animated: true,
                null);
        }
    }
}
