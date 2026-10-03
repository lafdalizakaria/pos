using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newrest.Pos.Client.Core.ViewModels;
using Newrest.Pos.Devices.Badges;

namespace Newrest.Pos.Client;

public partial class MainWindow : Window
{
    private readonly KeyboardWedgeBadgeReader? _wedge;

    public MainWindow(ShellViewModel shell, IBadgeReader badgeReader)
    {
        InitializeComponent();
        DataContext = shell;
        _wedge = badgeReader as KeyboardWedgeBadgeReader;
    }

    /// <summary>HID badge readers type fast and end with Enter: such bursts are captured as badges, not typed into fields.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_wedge is null || Keyboard.FocusedElement is PasswordBox)
        {
            return;
        }

        foreach (var c in e.Text)
        {
            _wedge.OnCharacter(c);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_wedge is not null && e.Key == Key.Enter && _wedge.OnCharacter('\r'))
        {
            e.Handled = true;
            if (Keyboard.FocusedElement is TextBox box && box.Tag is "badge-capture-clear")
            {
                box.Clear();
            }
        }
    }
}
