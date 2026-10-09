using Microsoft.UI.Xaml;

namespace Signet.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/Signet.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 820));
    }
}
