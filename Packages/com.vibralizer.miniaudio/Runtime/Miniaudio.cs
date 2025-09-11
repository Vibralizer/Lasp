using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiniaudioNative
{
    /// <summary>
    /// Thin P/Invoke wrapper over the native miniaudio Unity plugin
    /// implemented in ma_unity.cpp (capture + loopback when supported).
    /// </summary>
    public class Miniaudio
    {
        #region DLLName
        
#if UNITY_IOS || UNITY_TVOS
        private const string Dll = "__Internal";
#else
        // Adjust if your native plugin name differs.
        private const string Dll = "MiniaudioUnity";
#endif
        
        #endregion

        #region Public properties and methods

        // Context
        
        public static IntPtr CtxCreate()
            => _CtxCreate();
        public static void CtxDestroy(IntPtr ctx)
            => _CtxDestroy(ctx);
        
        // Input
        
        public static int InputCount(IntPtr ctx)
            => _InputCount(ctx);
        public static bool InputIsLoopback(IntPtr ctx, int logicalIndex)
            => _InputIsLoopback(ctx, logicalIndex) != 0;
        public static int InputDefaultIndex(IntPtr ctx)
            => _InputDefaultIndex(ctx);
        public static string InputName(IntPtr ctx, int logicalIndex)
            => PtrToAnsiString(_InputName(ctx, logicalIndex));
        public static string InputId(IntPtr ctx, int logicalIndex)
            => PtrToAnsiString(_InputId(ctx, logicalIndex));
        public static int InputChannels(IntPtr ctx, int logicalIndex)
            => _InputChannels(ctx, logicalIndex);
        public static int InputNativeRate(IntPtr ctx, int logicalIndex)
            => _InputNativeRate(ctx, logicalIndex);
        
        // Device
        
        public static IntPtr DeviceCreate(IntPtr ctx, int logicalIndex, int sampleRate, int channels, double softwareLatencySeconds)
            => _DeviceCreate(ctx, logicalIndex, sampleRate, channels, softwareLatencySeconds);
        public static void DeviceDestroy(IntPtr device)
            => _DeviceDestroy(device);
        public static void DeviceStart(IntPtr device)
            => _DeviceStart(device);
        public static void DeviceStop(IntPtr device)
            => _DeviceStop(device);
        public static void DeviceSetReadCallback(IntPtr device, DeviceReadCallback cb, IntPtr user)
            => _DeviceSetReadCallback(device, cb, user);
        public static int DeviceBytesPerFrame(IntPtr device)
            => _DeviceBytesPerFrame(device);
        public static int DeviceSampleRate(IntPtr device)
            => _DeviceSampleRate(device);
        
        #endregion
        
        
        #region Unmanaged functions

        // Miniaudio Context
        
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_ctx_create")]
        static extern IntPtr _CtxCreate();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_ctx_destroy")]
        static extern void _CtxDestroy(IntPtr ctx);

        // Input and Device enumeration (logical inputs: capture + loopback)

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_input_count")]
        static extern int _InputCount(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_input_is_loopback")]
        static extern int _InputIsLoopback(IntPtr ctx, int logicalIndex);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_input_default_index")]
        static extern int _InputDefaultIndex(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_input_name")]
        static extern IntPtr _InputName(IntPtr ctx, int logicalIndex);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_input_id")]
        static extern IntPtr _InputId(IntPtr ctx, int logicalIndex);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_input_channels")]
        static extern int _InputChannels(IntPtr ctx, int logicalIndex);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_input_native_rate")]
        static extern int _InputNativeRate(IntPtr ctx, int logicalIndex);

        // TODO: Is there a better way to do this?
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DeviceReadCallback(
            IntPtr user,            // GCHandle (Lasp InputDeviceHandle)
            IntPtr interleaved,     // float* interleaved input
            int frameCount,
            int bytesPerFrame);
        
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_device_create")]
        static extern IntPtr _DeviceCreate(
            IntPtr ctx,
            int logicalIndex,
            int sampleRate,     // 0 = native
            int channels,       // 0 = native
            double softwareLatencySeconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_device_destroy")]
        static extern void _DeviceDestroy(IntPtr device);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_device_start")]
        static extern void _DeviceStart(IntPtr device);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_device_stop")]
        static extern void _DeviceStop(IntPtr device);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_device_set_read_callback")]
        static extern void _DeviceSetReadCallback(IntPtr device, DeviceReadCallback cb, IntPtr user);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_device_bytes_per_frame")]
        static extern int _DeviceBytesPerFrame(IntPtr device);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "miniaudio_device_sample_rate")]
        static extern int _DeviceSampleRate(IntPtr device);

        internal static string PtrToAnsiString(IntPtr p) =>
            p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(p) ?? string.Empty;
        
        #endregion
    }
}