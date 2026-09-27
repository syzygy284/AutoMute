// Minimal hand-written interop for the Windows Core Audio APIs (WASAPI).
// Only what AutoMute needs: find the default output device, capture what it
// plays (loopback), mute/unmute it, and hear about default-device changes.
//
// IMPORTANT: in [ComImport] interfaces the method ORDER must match the native
// vtable exactly, so methods we never call are still declared as placeholders.

using System;
using System.Runtime.InteropServices;

namespace AutoMute
{
    public static class CoreAudio
    {
        public const int eRender = 0;
        public const int eConsole = 0;
        public const int eMultimedia = 1;

        public const int CLSCTX_ALL = 0x17;
        public const int AUDCLNT_SHAREMODE_SHARED = 0;
        public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        public const int STGM_READ = 0;
        public const short VT_LPWSTR = 31;

        public static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
        public static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        public static readonly Guid SUBTYPE_IEEE_FLOAT = new Guid("00000003-0000-0010-8000-00AA00389B71");
        public static readonly Guid PKEY_Device_FriendlyName_fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(IntPtr pvar);

        public static IMMDeviceEnumerator CreateEnumerator()
        {
            Type t = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            return (IMMDeviceEnumerator)Activator.CreateInstance(t);
        }

        public static object Activate(IMMDevice device, Guid iid)
        {
            object o;
            device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
            return o;
        }

        public static string GetFriendlyName(IMMDevice device)
        {
            IPropertyStore store = null;
            IntPtr pv = IntPtr.Zero;
            try
            {
                device.OpenPropertyStore(STGM_READ, out store);
                PropertyKey key = new PropertyKey(PKEY_Device_FriendlyName_fmtid, 14);
                pv = Marshal.AllocCoTaskMem(32);  // PROPVARIANT is 16 (x86) / 24 (x64) bytes
                for (int i = 0; i < 32; i++) Marshal.WriteByte(pv, i, 0);
                store.GetValue(ref key, pv);
                if (Marshal.ReadInt16(pv, 0) == VT_LPWSTR)
                {
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch
            {
                // name is cosmetic only
            }
            finally
            {
                if (pv != IntPtr.Zero)
                {
                    PropVariantClear(pv);
                    Marshal.FreeCoTaskMem(pv);
                }
                Release(store);
            }
            return "Default output device";
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o))
            {
                try { Marshal.ReleaseComObject(o); } catch { }
            }
        }
    }

    /// <summary>The shared-mode mix format of an endpoint (WAVEFORMATEX / WAVEFORMATEXTENSIBLE).</summary>
    public sealed class MixFormat
    {
        public int Channels;
        public int SampleRate;
        public int BitsPerSample;
        public bool IsFloat;

        public static MixFormat Read(IntPtr p)
        {
            MixFormat f = new MixFormat();
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            f.Channels = (ushort)Marshal.ReadInt16(p, 2);
            f.SampleRate = Marshal.ReadInt32(p, 4);
            f.BitsPerSample = (ushort)Marshal.ReadInt16(p, 14);
            f.IsFloat = tag == 3; // WAVE_FORMAT_IEEE_FLOAT
            if (tag == 0xFFFE)    // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID at offset 24
            {
                byte[] g = new byte[16];
                Marshal.Copy(IntPtr.Add(p, 24), g, 0, 16);
                f.IsFloat = new Guid(g) == CoreAudio.SUBTYPE_IEEE_FLOAT;
            }
            return f;
        }

        public override string ToString()
        {
            return SampleRate + " Hz, " + Channels + " ch, " + BitsPerSample + "-bit " + (IsFloat ? "float" : "PCM");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid fmtid, int pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices); // placeholder
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IMMNotificationClient client);
        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        void OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, IntPtr propVariant);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        void GetBufferSize(out int frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out int frames);
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out int numFrames, out int flags, out long devicePosition, out long qpcPosition);
        void ReleaseBuffer(int numFrames);
        void GetNextPacketSize(out int numFrames);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out int count);
        void SetMasterVolumeLevel(float levelDb, IntPtr eventContext);
        void SetMasterVolumeLevelScalar(float level, IntPtr eventContext);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(int channel, float levelDb, IntPtr eventContext);
        void SetChannelVolumeLevelScalar(int channel, float level, IntPtr eventContext);
        void GetChannelVolumeLevel(int channel, out float levelDb);
        void GetChannelVolumeLevelScalar(int channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Receives device notifications from Windows. Called on a system
    /// thread, so it only sets a flag - never touches audio objects directly.</summary>
    [ComVisible(true)]
    public sealed class DeviceNotifier : IMMNotificationClient
    {
        private readonly AudioEngine engine;

        public DeviceNotifier(AudioEngine engine)
        {
            this.engine = engine;
        }

        public void OnDeviceStateChanged(string deviceId, int newState) { }
        public void OnDeviceAdded(string deviceId) { }
        public void OnDeviceRemoved(string deviceId) { }

        public void OnDefaultDeviceChanged(int dataFlow, int role, string defaultDeviceId)
        {
            if (dataFlow == CoreAudio.eRender) engine.RequestRestart();
        }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
