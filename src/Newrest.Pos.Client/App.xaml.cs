using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Newrest.Pos.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // French UI; Arabic (RTL) will switch FlowDirection at window level.
        var culture = new CultureInfo("fr-MA");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        base.OnStartup(e);
    }
}
