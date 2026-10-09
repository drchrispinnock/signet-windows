using Microsoft.UI.Xaml;

namespace Signet.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/Signet.ico");
    }
}
