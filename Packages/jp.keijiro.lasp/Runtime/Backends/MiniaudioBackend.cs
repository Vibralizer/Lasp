#if LASP_BACKEND_MINIAUDIO
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PInvokeCallbackAttribute = AOT.MonoPInvokeCallbackAttribute;
using static Lasp.Backends.MiniaudioNative;

namespace Lasp.Backends
{
    internal sealed class AudioBackend : IAudioBackend
    {
        public IContext CreateContext() => new Context();
    }

    internal sealed class Context : IContext
    {
        private IntPtr _ctx;

        // Native devices-changed thunk must be static for AOT; we route to the single live Context.
        private static readonly DevicesChangedCallback sDevicesChangedThunk = OnDevicesChangeThunkStatic;
        private static Context sLiveContext; // LASP uses one context; map thunk → instance.

        // Store the managed delegate (rooted here).
        private IContext.OnDevicesChangeDelegate _managedDevicesChanged;

        public Context()
        {
            _ctx = CtxCreate();
            sLiveContext = this;
            CtxSetDevicesChangedCallback(_ctx, sDevicesChangedThunk);
        }

        public IContext.OnDevicesChangeDelegate OnDevicesChange
        {
            get => _managedDevicesChanged;
            set => _managedDevicesChanged = value;
        }

        [PInvokeCallback(typeof(DevicesChangedCallback))]
        private static void OnDevicesChangeThunkStatic()
        {
            // Miniaudio callback carries no context pointer; LASP has one context, so use the live one.
            sLiveContext?._managedDevicesChanged?.Invoke(IntPtr.Zero);
        }

        public void Connect()     => CtxConnect(_ctx, -1 /* default backend */);
        public void FlushEvents() => CtxFlushEvents(_ctx);

        public int InputDeviceCount        => CtxGetInputDeviceCount(_ctx);
        public int DefaultInputDeviceIndex => CtxGetDefaultInputDeviceIndex(_ctx);

        static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return string.Empty;
            int len = 0; while (Marshal.ReadByte(p, len) != 0) len++;
            var buf = new byte[len]; Marshal.Copy(p, buf, 0, len);
            return System.Text.Encoding.UTF8.GetString(buf);
        }

        public IDevice GetInputDevice(int index)
        {
            return new Device(
                this,
                index,
                Utf8(CtxGetInputDeviceId(_ctx, index)),
                Utf8(CtxGetInputDeviceName(_ctx, index)),
                CtxGetInputDeviceChannels(_ctx, index),
                CtxGetInputDeviceNativeRate(_ctx, index)
            );
        }

        public void Dispose()
        {
            if (_ctx != IntPtr.Zero)
            {
                // Optional: clear callback on native side if your plugin supports it.
                CtxDestroy(_ctx);
                _ctx = IntPtr.Zero;
            }
            if (ReferenceEquals(sLiveContext, this)) sLiveContext = null;
            _managedDevicesChanged = null;
        }

        internal IntPtr Handle => _ctx;
    }

    internal sealed class Device : IDevice
    {
        readonly Context _ctx;
        public int Index { get; }
        public string ID   { get; }
        public string Name { get; }
        public int    ChannelCount { get; }
        public int[]  SampleRates { get; }
        public bool   IsRaw => false; // LASP filters raw devices; miniaudio doesn’t expose a separate “raw” flag
        public double SoftwareLatencyMin => 0.0; // not queried from miniaudio; LASP will clamp to >= 1/60

        public Device(Context ctx, int index, string id, string name, int ch, int nativeRate)
        {
            _ctx = ctx; Index = index; ID = id; Name = name; ChannelCount = ch;
            SampleRates = nativeRate > 0 ? new[] { nativeRate } : Array.Empty<int>();
        }

        public IInStream CreateInStream() => new Stream(_ctx, this);
        public void Dispose() { /* miniaudio device objects are owned by the context */ }

        internal Context Ctx => _ctx;
    }

    internal sealed class Stream : IInStream
    {
        readonly Context _ctx;
        readonly Device  _dev;

        IntPtr _stream;
        bool   _started;

        public Stream(Context ctx, Device dev) { _ctx = ctx; _dev = dev; }

        // LASP sets these prior to Open(); 0 means “native”.
        public int    SampleRate      { get; set; }   // 0 = native
        public int    ChannelCount    { get; set; }   // 0 = native
        public double SoftwareLatency { get; set; }   // seconds

        public int  BytesPerFrame => _stream == IntPtr.Zero ? 0 : StreamGetBytesPerFrame(_stream);
        public bool IsActive      => _stream != IntPtr.Zero && _started;
        public IntPtr UserData    { get; set; } // GCHandle to InputDeviceHandle (set by LASP)

        // AOT-safe rooted thunks (native → managed)
        static readonly ReadCallback     sRead     = OnReadThunk;
        static readonly OverflowCallback sOverflow = OnOverflowThunk;
        static readonly ErrorCallback    sError    = OnErrorThunk;

        // Map InputDeviceHandle GCHandle (UserData) → this Stream, to route thunks.
        static readonly Dictionary<IntPtr, Stream> sMap = new Dictionary<IntPtr, Stream>();

        public void Open()
        {
            if (_stream != IntPtr.Zero) return;

            _stream = StreamCreate(
                _ctx.Handle,
                _dev.Index,
                loopback: 0,                  // capture by default; loopback can be a separate “device kind”
                sampleRate: SampleRate,       // 0 = native
                channels:   ChannelCount,     // 0 = native
                softwareLatency: SoftwareLatency
            );

            if (_stream == IntPtr.Zero)
                throw new InvalidOperationException("Miniaudio StreamCreate failed");

            // Install callbacks and set LASP’s GCHandle as native UserData
            StreamSetCallbacks(_stream, sRead, sOverflow, sError);

            // Register this stream for thunk routing only after a successful open
            StreamSetUserData(_stream, UserData);
            lock (sMap) sMap[UserData] = this;

            // Capture negotiated format so LASP can size its buffers.
            SampleRate = StreamGetSampleRate(_stream);

            // If ChannelCount wasn’t requested, infer from device or bytes/frame (float32 interleaved).
            if (ChannelCount <= 0)
            {
                var bpf = BytesPerFrame;
                if (bpf > 0) ChannelCount = Math.Max(1, bpf / 4);
                if (ChannelCount <= 0 && _dev.ChannelCount > 0) ChannelCount = _dev.ChannelCount;
                if (ChannelCount <= 0) ChannelCount = 1;
            }
        }

        public void Start()
        {
            if (_stream == IntPtr.Zero) throw new InvalidOperationException("Stream not opened");
            if (_started) return;
            StreamStart(_stream);
            _started = true;
        }

        public void Stop()
        {
            if (_stream == IntPtr.Zero) return;
            StreamStop(_stream);
            _started = false;

            lock (sMap) sMap.Remove(UserData);
            StreamDestroy(_stream);
            _stream = IntPtr.Zero;
        }

        public void Dispose() => Stop();

        // ===== Native → managed thunks ===========================================

        [PInvokeCallback(typeof(ReadCallback))]
        static void OnReadThunk(ref IInStream.InStreamData s, int min, int left)
        {
            if (!TryGetSelf(s.UserData, out var self)) return;
            self.ReadCallback?.Invoke(ref s, min, left);
        }

        [PInvokeCallback(typeof(OverflowCallback))]
        static void OnOverflowThunk(ref IInStream.InStreamData s)
        {
            if (!TryGetSelf(s.UserData, out var self)) return;
            self.OverflowCallback?.Invoke(ref s);
        }

        [PInvokeCallback(typeof(ErrorCallback))]
        static void OnErrorThunk(ref IInStream.InStreamData s, int err)
        {
            if (!TryGetSelf(s.UserData, out var self)) return;
            self.ErrorCallback?.Invoke(ref s, err);
        }

        static bool TryGetSelf(IntPtr key, out Stream self)
        {
            lock (sMap) return sMap.TryGetValue(key, out self);
        }

        // ===== IInStream: BeginRead / EndRead (miniaudio variant) ================

        public unsafe void BeginRead(ref IInStream.InStreamData _,
                                     out IInStream.ChannelArea* areas,
                                     ref int frameCount)
        {
            // Miniaudio hands back a single interleaved area with Step = BytesPerFrame
            MiniaudioNative.ChannelArea* nativeAreas;
            StreamBeginRead(_stream, out nativeAreas, ref frameCount);
            areas = (IInStream.ChannelArea*)nativeAreas;
        }

        public void EndRead(ref IInStream.InStreamData _)
        {
            StreamEndRead(_stream);
        }

        // Expose handlers so InputDeviceHandle can assign them.
        public IInStream.ReadCallbackDelegate      ReadCallback     { get; set; }
        public IInStream.OverflowCallbackDelegate  OverflowCallback { get; set; }
        public IInStream.ErrorCallbackDelegate     ErrorCallback    { get; set; }
    }
}
#endif // LASP_BACKEND_MINIAUDIO
