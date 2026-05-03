using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LanScreenShare.Core.Models;
using LanScreenShare.Network;
using System.Collections.ObjectModel;
using System.Linq;

namespace LanScreenShare.Desktop;

public partial class MainWindow : Window
{
    private DiscoveryService _discoveryService;
    public ObservableCollection<DeviceInfo> Devices { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        _discoveryService = new DiscoveryService();
        _discoveryService.OnDeviceDiscovered += DiscoveryService_OnDeviceDiscovered;

        Devices = new ObservableCollection<DeviceInfo>();
        
        var devicesList = this.FindControl<ListBox>("DevicesList");
        if (devicesList != null)
        {
            devicesList.ItemsSource = Devices;
        }

        var hostButton = this.FindControl<Button>("HostButton");
        if (hostButton != null)
        {
            hostButton.Click += HostButton_Click;
        }

        var searchButton = this.FindControl<Button>("SearchButton");
        if (searchButton != null)
        {
            searchButton.Click += SearchButton_Click;
        }
    }

    private void HostButton_Click(object? sender, RoutedEventArgs e)
    {
        _discoveryService.StartHostingBroadcast();
        
        if (sender is Button hostButton)
        {
            hostButton.Content = "Hosting UDP...";
            hostButton.IsEnabled = false;
        }
    }

    private void SearchButton_Click(object? sender, RoutedEventArgs e)
    {
        _discoveryService.StartListening();
        
        if (sender is Button searchButton)
        {
            searchButton.Content = "Buscando...";
            searchButton.IsEnabled = false;
        }
    }

    private void DiscoveryService_OnDeviceDiscovered(object? sender, DeviceInfo e)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var existing = Devices.FirstOrDefault(d => d.IPAddress == e.IPAddress);
            if (existing != null)
            {
                existing.LastSeen = e.LastSeen;
                existing.Name = e.Name;
                // Para refrescar la UI de un item, normalmente se implementa INotifyPropertyChanged en DeviceInfo.
                // Aquí por simplicidad podemos forzar la actualización o usar ObservableCollection de forma que lo note.
            }
            else
            {
                Devices.Add(e);
            }
        });
    }
}