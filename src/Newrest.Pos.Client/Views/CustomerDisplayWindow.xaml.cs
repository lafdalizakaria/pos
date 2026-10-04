using System.Windows;
using Newrest.Pos.Client.Infrastructure;

namespace Newrest.Pos.Client.Views;

public partial class CustomerDisplayWindow : Window
{
    public CustomerDisplayWindow(WindowCustomerDisplay display)
    {
        InitializeComponent();
        DataContext = display;
    }

    /// <summary>
    /// Opens full screen on the second monitor when the virtual desktop is wider than the primary screen
    /// (customer screen configured to the right of the cashier screen); otherwise not shown.
    /// </summary>
    public static void ShowOnSecondaryScreen(WindowCustomerDisplay display)
    {
        if (SystemParameters.VirtualScreenWidth <= SystemParameters.PrimaryScreenWidth)
        {
            return;
        }

        var window = new CustomerDisplayWindow(display)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.PrimaryScreenWidth,
            Top = 0,
            Width = SystemParameters.VirtualScreenWidth - SystemParameters.PrimaryScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
        };
        window.Show();
        window.WindowState = WindowState.Maximized;
    }
}
