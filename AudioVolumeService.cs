using System;
using System.Runtime.InteropServices;

namespace WindowsNotch
{
    /// <summary>
    /// Mengontrol volume master speaker Windows melalui Core Audio API
    /// (IMMDeviceEnumerator + IAudioEndpointVolume). Tidak memerlukan paket eksternal.
    /// </summary>
    public sealed class AudioVolumeService : IDisposable
    {
        private const string MmDeviceEnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";
        private const string MmDeviceEnumeratorIid = "A95664D2-9614-4F35-A746-DE8DB63617E6";
        private const string AudioEndpointVolumeIid = "5CDF2C82-841E-4546-9722-0CF74078229A";

        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioEndpointVolume? _endpointVolume;
        private bool _disposed;

        /// <summary>Volume master 0..1 (0 = senyap, 1 = maksimum).</summary>
        public float Volume
        {
            get
            {
                EnsureEndpoint();
                if (_endpointVolume == null) return 0f;
                try
                {
                    return _endpointVolume.GetMasterVolumeLevelScalar(out float volume) >= 0 ? volume : 0f;
                }
                catch
                {
                    return 0f;
                }
            }
            set
            {
                EnsureEndpoint();
                if (_endpointVolume == null) return;
                var v = Math.Clamp(value, 0f, 1f);
                try { _endpointVolume.SetMasterVolumeLevelScalar(v, IntPtr.Zero); }
                catch { }
            }
        }

        public bool IsMuted
        {
            get
            {
                EnsureEndpoint();
                if (_endpointVolume == null) return false;
                try
                {
                    return _endpointVolume.GetMute(out bool muted) >= 0 && muted;
                }
                catch
                {
                    return false;
                }
            }
            set
            {
                EnsureEndpoint();
                if (_endpointVolume == null) return;
                try { _endpointVolume.SetMute(value, IntPtr.Zero); }
                catch { }
            }
        }

        public AudioVolumeService()
        {
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid(MmDeviceEnumeratorClsid));
                if (type == null) return;

                _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
                if (_enumerator.GetDefaultAudioEndpoint(0, 1, out _device) >= 0 && _device != null)
                {
                    OpenEndpointVolume();
                }
            }
            catch
            {
                // Audio API tidak tersedia -> volume control diam saja
            }
        }

        private void OpenEndpointVolume()
        {
            if (_device == null) return;

            var iid = typeof(IAudioEndpointVolume).GUID;
            int hr = _device.Activate(ref iid, 0, IntPtr.Zero, out object epvObj);
            if (hr < 0) return;

            _endpointVolume = epvObj as IAudioEndpointVolume;
        }

        private void EnsureEndpoint()
        {
            if (_endpointVolume != null || _disposed) return;

            try
            {
                if (_device == null && _enumerator != null)
                {
                    _enumerator.GetDefaultAudioEndpoint(0, 1, out _device);
                }
                OpenEndpointVolume();
            }
            catch { _endpointVolume = null; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_endpointVolume != null)
                {
                    Marshal.ReleaseComObject(_endpointVolume);
                    _endpointVolume = null;
                }
            }
            catch { }

            try
            {
                if (_device != null)
                {
                    Marshal.ReleaseComObject(_device);
                    _device = null;
                }
            }
            catch { }

            try
            {
                if (_enumerator != null)
                {
                    Marshal.ReleaseComObject(_enumerator);
                    _enumerator = null;
                }
            }
            catch { }
        }

        // ===================== COM interop =====================
        // Urutan method HARUS sama persis dengan vtable COM asli.
        // Catatan: SetMasterVolumeLevelScalar berada SEBELUM GetMasterVolumeLevel
        // (urutan vtable biner, bukan urutan alfabet dokumentasi).

        [ComImport]
        [Guid(MmDeviceEnumeratorIid)]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int state, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            int GetDevice(string pwstrId, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppStore);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [ComImport]
        [Guid(AudioEndpointVolumeIid)]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out uint pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, IntPtr pguidEventContext);
            int SetMasterVolumeLevelScalar(float fLevel, IntPtr pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int SetChannelVolumeLevel(uint nChannel, float fLevelDB, IntPtr pguidEventContext);
            int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, IntPtr pguidEventContext);
            int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, IntPtr pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
            int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
            int VolumeStepUp(IntPtr pguidEventContext);
            int VolumeStepDown(IntPtr pguidEventContext);
            int QueryHardwareSupport(out uint pdwHardwareSupportMask);
            int GetVolumeRange(out float pflVolumeMin, out float pflVolumeMax, out float pflVolumeIncrement);
        }
    }
}
