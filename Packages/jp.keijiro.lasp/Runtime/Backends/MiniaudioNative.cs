#if LASP_BACKEND_MINIAUDIO
using System;
using System.Runtime.InteropServices;

namespace Lasp.Backends
{
    internal static class MiniaudioNative
    {
#if UNITY_IOS || UNITY_TVOS
        const string Dll = "__Internal";
#else
        const string Dll = "MiniaudioUnity";
#endif

        // ====== Context / devices =================================================

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CtxCreate();  // returns ma_context*

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CtxDestroy(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CtxConnect(IntPtr ctx, int backend /*-1=default*/);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CtxFlushEvents(IntPtr ctx);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void DevicesChangedCallback();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void CtxSetDevicesChangedCallback(IntPtr ctx, DevicesChangedCallback cb);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int  CtxGetInputDeviceCount(IntPtr ctx);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int  CtxGetDefaultInputDeviceIndex(IntPtr ctx);

        // Returns a pointer to a stable UTF8 string; caller copies to managed string
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CtxGetInputDeviceId(IntPtr ctx, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr CtxGetInputDeviceName(IntPtr ctx, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int  CtxGetInputDeviceChannels(IntPtr ctx, int index);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int  CtxGetInputDeviceNativeRate(IntPtr ctx, int index);

        // ====== Stream ============================================================

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ReadCallback(ref IInStream.InStreamData stream, int min, int left);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void OverflowCallback(ref IInStream.InStreamData stream);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ErrorCallback(ref IInStream.InStreamData stream, int error);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr StreamCreate(               // returns stream*
            IntPtr ctx,
            int    deviceIndex,      // from input device list
            int    loopback,         // 0=capture, 1=loopback (WASAPI)
            int    sampleRate,       // 0=native
            int    channels,         // 0=native
            double softwareLatency   // seconds; 0=backend default/min
        );

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void StreamDestroy(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void StreamSetCallbacks(
            IntPtr stream,
            ReadCallback     onRead,
            OverflowCallback onOverflow,
            ErrorCallback    onError
        );

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void StreamSetUserData(IntPtr stream, IntPtr userData); // GCHandle

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int  StreamGetBytesPerFrame(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int  StreamGetSampleRate(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void StreamStart(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void StreamStop(IntPtr stream);

        // ====== BeginRead / EndRead, used inside the managed Read callback =========
        // We return 1 channel area that points to an interleaved buffer, and set Step = bytesPerFrame.
        public unsafe struct ChannelArea { public byte* Pointer; public int Step; }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public unsafe static extern void StreamBeginRead(
            IntPtr stream,
            out ChannelArea* areas,
            ref int frameCount
        );

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void StreamEndRead(IntPtr stream);
    }
}
#endif // LASP_BACKEND_MINIAUDIO
