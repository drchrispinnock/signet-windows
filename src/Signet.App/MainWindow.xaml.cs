using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Signet.App.Views;
using Signet.ViewModels;

namespace Signet.App;

/// <summary>The single window (upstream's <c>Window("Signet", id: "main")</c>), showing <c>WalletHomeView</c>.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(WalletViewModel model)
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/Signet.ico");
        // WalletHomeView's frame: at least 480 × 720, as on the Mac.
        var scale = (Content?.XamlRoot?.RasterizationScale) ?? 1;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(560 * scale), (int)(860 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 480;
            presenter.PreferredMinimumHeight = 720;
        }
        Host.Content = new WalletHomeView(model);
    }
}
