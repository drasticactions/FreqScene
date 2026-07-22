using Android.App;

[assembly: Application(Label = "FreqScene", UsesCleartextTraffic = true)]

[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
// NsdManager normally manages multicast itself, but some OEM builds still
// require the permission for mDNS discovery to deliver results.
[assembly: UsesPermission(Android.Manifest.Permission.ChangeWifiMulticastState)]

// TV-friendly, not TV-only: leanback launcher works, nothing requires touch.
[assembly: UsesFeature("android.software.leanback", Required = false)]
[assembly: UsesFeature("android.hardware.touchscreen", Required = false)]
