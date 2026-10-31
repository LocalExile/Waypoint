using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Launcher.Core;

namespace Launcher.Desktop;

public partial class MainWindow
{
    readonly List<ServiceStatusIndicator> serviceIndicators = [];

    void InitializeServiceIndicators()
    {
        foreach (string name in new[] { "Patch", "Gateway", "Auth", "Game" })
        {
            var indicator = new ServiceStatusIndicator(name);
            indicator.Click += (_, _) =>
            {
                NavigateTo(0);
                var row = serviceRows.FirstOrDefault(row => row.Value.Name == name);
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (row is not null && ServiceList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
                    { container.BringIntoView(); container.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); }
                }));
            };
            serviceIndicators.Add(indicator); ServiceIndicatorStrip.Children.Add(indicator);
        }
    }
    void UpdateServiceIndicators(RuntimeSnapshot? state)
    {
        foreach (var indicator in serviceIndicators)
            indicator.Update(state?.Services.FirstOrDefault(service => service.Name == indicator.ServiceName));
    }
    internal void RefreshServiceIndicatorAppearance()
    {
        foreach (var indicator in serviceIndicators) indicator.RefreshAppearance();
    }
}
