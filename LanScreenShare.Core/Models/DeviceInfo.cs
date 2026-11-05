using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanScreenShare.Core.Models
{
    public class DeviceInfo : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _ipAddress = string.Empty;
        private DateTime _lastSeen;
        private bool _isRemoteControlAllowed = true;
        private bool _isViewing;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                }
            }
        }

        public string IPAddress
        {
            get => _ipAddress;
            set
            {
                if (_ipAddress != value)
                {
                    _ipAddress = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime LastSeen
        {
            get => _lastSeen;
            set
            {
                if (_lastSeen != value)
                {
                    _lastSeen = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsRemoteControlAllowed
        {
            get => _isRemoteControlAllowed;
            set
            {
                if (_isRemoteControlAllowed != value)
                {
                    _isRemoteControlAllowed = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsViewing
        {
            get => _isViewing;
            set
            {
                if (_isViewing != value)
                {
                    _isViewing = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActionButtonText));
                }
            }
        }

        public string ActionButtonText => _isViewing ? "Viendo" : "Ver pantalla";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
