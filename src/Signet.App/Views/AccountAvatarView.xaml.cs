using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signet.App.Services;
using Signet.Models;
using Signet.Services;

namespace Signet.App.Views;

/// <summary>
/// Round avatar for an address from TzKT's avatar service (TzProfiles logo, known-account logo,
/// or an identicon). Always mainnet: a TzProfile is a mainnet identity.
/// </summary>
public sealed partial class AccountAvatarView : UserControl
{
    public AccountAvatarView()
    {
        InitializeComponent();
        Apply();
    }

    public AccountAvatarView(Address address, double size = 44) : this()
    {
        Size = size;
        Address = address;
    }

    public Address? Address
    {
        get => address;
        set
        {
            if (value == address) return;
            address = value;
            Load();
        }
    }
    private Address? address;

    public double Size
    {
        get => size;
        set { size = value; Apply(); }
    }
    private double size = 44;

    private void Apply()
    {
        Width = Height = size;
        Placeholder.FontSize = size * 0.45;
    }

    private void Load()
    {
        Placeholder.Visibility = Visibility.Visible;
        if (address is not { } value || Profiles.AvatarURL(value) is not { } url)
        {
            Picture.Source = null;
            return;
        }
        ImageLoader.Load(Picture, [url], (int)Math.Ceiling(size * 2), ok => Placeholder.Visibility = ok ? Visibility.Collapsed : Visibility.Visible);
    }
}
