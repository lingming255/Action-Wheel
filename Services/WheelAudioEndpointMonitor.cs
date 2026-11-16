using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Action_Wheel.Services
{
    /// <summary>CoreAudio notifications only invalidate worker-owned waveOut handles.</summary>
    internal sealed class WheelAudioEndpointMonitor : IDisposable
    {
        private IDeviceEnumerator? _enumerator;
        private readonly Notifications _notifications;
        private long _generation;
        private string? _endpointId;
        internal int NotificationRegistrationResult { get; private set; } = unchecked((int)0x80004005);
        internal long Generation => Volatile.Read(ref _generation);
        internal string? DefaultEndpointId => Volatile.Read(ref _endpointId);

        internal WheelAudioEndpointMonitor()
        {
            _notifications = new Notifications(this);
            try
            {
                _enumerator = (IDeviceEnumerator)(object)new DeviceEnumerator();
                NotificationRegistrationResult = _enumerator.RegisterEndpointNotificationCallback(_notifications);
                RefreshDefaultEndpoint();
            }
            catch (Exception) { Dispose(); }
        }

        internal void RefreshDefaultEndpoint()
        {
            // Polling also covers notification registration failure without touching the UI path.
            IDevice? device = null;
            try
            {
                if (_enumerator == null) return;
                string? id = null;
                if (_enumerator.GetDefaultAudioEndpoint(0, 1, out device) == 0)
                    Marshal.ThrowExceptionForHR(device.GetId(out id));
                if (string.Equals(id, DefaultEndpointId, StringComparison.OrdinalIgnoreCase)) return;
                Volatile.Write(ref _endpointId, id);
                Interlocked.Increment(ref _generation);
            }
            catch (Exception)
            {
                // Audio is optional; the next request can retry the default endpoint query.
            }
            finally { if (device != null) Marshal.ReleaseComObject(device); }
        }

        public void Dispose()
        {
            IDeviceEnumerator? enumerator = _enumerator;
            _enumerator = null;
            if (enumerator == null) return;
            try { enumerator.UnregisterEndpointNotificationCallback(_notifications); }
            catch (Exception) { }
            Marshal.ReleaseComObject(enumerator);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class Notifications : INotifications
        {
            private readonly WheelAudioEndpointMonitor _owner;
            internal Notifications(WheelAudioEndpointMonitor owner) => _owner = owner;
            public int OnDeviceStateChanged(string id, uint state) => InvalidateIfSelected(id);
            public int OnDeviceAdded(string id) => InvalidateIfSelected(id);
            public int OnDeviceRemoved(string id) => InvalidateIfSelected(id);
            public int OnDefaultDeviceChanged(int flow, int role, string? id)
            {
                if (flow != 0 || role != 1) return 0; // render / multimedia
                Volatile.Write(ref _owner._endpointId, id);
                Interlocked.Increment(ref _owner._generation);
                return 0;
            }
            public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
            private int InvalidateIfSelected(string id)
            {
                if (string.Equals(id, _owner.DefaultEndpointId, StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref _owner._generation);
                return 0;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey { internal Guid Format; internal uint Id; }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private sealed class DeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(INotifications notifications);
            [PreserveSig] int UnregisterEndpointNotificationCallback(INotifications notifications);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDevice
        {
            [PreserveSig] int Activate(ref Guid id, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
            [PreserveSig] int OpenPropertyStore(uint mode, out nint store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out uint state);
        }

        [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INotifications
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
        }
    }
}
