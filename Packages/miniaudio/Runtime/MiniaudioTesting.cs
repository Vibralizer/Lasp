using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MiniaudioTesting
{
    internal static class Native
    {
#if UNITY_IOS && !UNITY_EDITOR
        const string Dll = "__Internal";
#else
        const string Dll = "MiniaudioUnity"; // your plugin name (without extension)
#endif

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct DeviceInfo
        {
            public int isDefault; // 0/1
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string name;
        }

        // Context
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mu_ctx_create();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void   mu_ctx_destroy(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_ctx_is_loopback_supported(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_ctx_get_capture_device_count(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_ctx_get_playback_device_count(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_ctx_get_capture_device_info(IntPtr ctx, int index, out DeviceInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_ctx_get_playback_device_info(IntPtr ctx, int index, out DeviceInfo info);

        // Device (capture/loopback), pull model (float32 interleaved).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mu_device_create_capture_default(IntPtr ctx, uint sampleRate, uint channels, uint ringBufferFrames);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr mu_device_create_loopback_default(IntPtr ctx, uint sampleRate, uint channels, uint ringBufferFrames);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_device_start(IntPtr dev);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_device_stop(IntPtr dev);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void   mu_device_destroy(IntPtr dev);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_device_read_f32(IntPtr dev, IntPtr dst, int frameCapacity);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint   mu_device_get_channels(IntPtr dev);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern uint   mu_device_get_sample_rate(IntPtr dev);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int    mu_device_get_name(IntPtr dev, StringBuilder dst, int cap);
    }

    /// <summary>Miniaudio Context (one per project or subsystem).</summary>
    public sealed class Context : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public bool IsLoopbackSupported => Handle != IntPtr.Zero && Native.mu_ctx_is_loopback_supported(Handle) != 0;

        public Context()
        {
            Handle = Native.mu_ctx_create();
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("miniaudio context init failed.");
        }

        public int CaptureDeviceCount => Handle == IntPtr.Zero ? 0 : Native.mu_ctx_get_capture_device_count(Handle);
        public int PlaybackDeviceCount => Handle == IntPtr.Zero ? 0 : Native.mu_ctx_get_playback_device_count(Handle);

        public (string name, bool isDefault) GetCaptureDeviceInfo(int index)
        {
            Native.DeviceInfo info;
            if (Native.mu_ctx_get_capture_device_info(Handle, index, out info) == 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            return (info.name, info.isDefault != 0);
        }

        public (string name, bool isDefault) GetPlaybackDeviceInfo(int index)
        {
            Native.DeviceInfo info;
            if (Native.mu_ctx_get_playback_device_info(Handle, index, out info) == 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            return (info.name, info.isDefault != 0);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { Native.mu_ctx_destroy(Handle); Handle = IntPtr.Zero; }
            GC.SuppressFinalize(this);
        }

        ~Context() { Dispose(); }
    }

    /// <summary>Miniaudio capture or loopback device with a pull API (float32 interleaved).</summary>
    public sealed class Device : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public uint Channels { get; private set; }
        public uint SampleRate { get; private set; }
        public string Name { get; private set; }

        Device(IntPtr handle)
        {
            Handle = handle;
            Channels   = Native.mu_device_get_channels(Handle);
            SampleRate = Native.mu_device_get_sample_rate(Handle);
            var sb = new StringBuilder(256);
            if (Native.mu_device_get_name(Handle, sb, sb.Capacity) != 0) Name = sb.ToString(); else Name = "(unknown)";
        }

        public static Device CreateDefaultCapture(Context ctx, uint sampleRate, uint channels, uint ringBufferFrames = 0)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var h = Native.mu_device_create_capture_default(ctx.Handle, sampleRate, channels, ringBufferFrames);
            if (h == IntPtr.Zero) throw new InvalidOperationException("Failed to create capture device.");
            return new Device(h);
        }

        public static Device CreateDefaultLoopback(Context ctx, uint sampleRate, uint channels, uint ringBufferFrames = 0)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            var h = Native.mu_device_create_loopback_default(ctx.Handle, sampleRate, channels, ringBufferFrames);
            if (h == IntPtr.Zero) throw new InvalidOperationException("Failed to create loopback device (unsupported backend?).");
            return new Device(h);
        }

        public void Start()
        {
            if (Native.mu_device_start(Handle) == 0)
                throw new InvalidOperationException("Device start failed.");
        }

        public void Stop()
        {
            if (Handle != IntPtr.Zero) _ = Native.mu_device_stop(Handle);
        }

        /// <summary>Reads up to frameCapacity frames into dst (float32 interleaved). Returns frames actually read.</summary>
        public unsafe int Read(float[] dst, int frameCapacity)
        {
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (frameCapacity <= 0) return 0;
            int samples = frameCapacity * checked((int)Channels);
            if (dst.Length < samples) throw new ArgumentException("dst shorter than frames*channels");

            fixed (float* p = dst)
            {
                return Native.mu_device_read_f32(Handle, (IntPtr)p, frameCapacity);
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { Stop(); Native.mu_device_destroy(Handle); Handle = IntPtr.Zero; }
            GC.SuppressFinalize(this);
        }

        ~Device() { Dispose(); }
    }
}
