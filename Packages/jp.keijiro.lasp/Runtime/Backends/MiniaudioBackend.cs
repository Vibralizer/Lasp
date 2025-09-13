#if LASP_BACKEND_MINIAUDIO
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using PInvokeCallbackAttribute = AOT.MonoPInvokeCallbackAttribute;
using MiniaudioNative;

namespace Lasp.Backends
{
    /// <summary>Miniaudio LASP backend (capture + loopback when available)</summary>
    internal sealed class AudioBackend : IAudioBackend
    {
        public IContext CreateContext() => new Context();
    }

    internal sealed class Context : IContext
    {
        private IntPtr _ctx;
        private IContext.OnDevicesChangeDelegate _managedDevicesChanged;
        private string _deviceSnapshot; // for polling-based device change detection

        public Context()
        {
            _ctx = Miniaudio.CtxCreate();
            if (_ctx == IntPtr.Zero)
                throw new InvalidOperationException("Miniaudio: failed to initialize context");
        }

        public IContext.OnDevicesChangeDelegate OnDevicesChange
        {
            get => _managedDevicesChanged;
            set => _managedDevicesChanged = value;
        }

        // Miniaudio does not require an explicit "connect" or event pump. We poll in FlushEvents.
        public void Connect() => _deviceSnapshot = BuildDeviceSnapshot();

        // Miniaudio has no event pump. Do nothing here.
        public void FlushEvents() { }

        public int InputDeviceCount => Miniaudio.InputCount(_ctx);
        public int DefaultInputDeviceIndex => Miniaudio.InputDefaultIndex(_ctx);

        public IDevice GetInputDevice(int index) => new Device(_ctx, index);

        public void Dispose()
        {
            _managedDevicesChanged = null;
            if (_ctx != IntPtr.Zero)
            {
                Miniaudio.CtxDestroy(_ctx);
                _ctx = IntPtr.Zero;
            }
        }

        private string BuildDeviceSnapshot()
        {
            int n = Miniaudio.InputCount(_ctx);
            var sb = new StringBuilder(n * 32);
            sb.Append(n).Append(';');
            for (int i = 0; i < n; i++)
            {
                var id   = Miniaudio.InputId(_ctx, i);
                var name = Miniaudio.InputName(_ctx, i);
                int ch   = Miniaudio.InputChannels(_ctx, i);
                bool lb   = Miniaudio.InputIsLoopback(_ctx, i);
                sb.Append(id).Append('|').Append(ch).Append('|').Append(lb).Append('|').Append(name).Append(';');
            }
            return sb.ToString();
        }
    }

    internal sealed class Device : IDevice
    {
        private readonly IntPtr _ctx;
        private readonly int _index;
        private readonly string _id;
        private readonly string _name;
        private readonly int _channels;
        private readonly int _nativeRate;

        public Device(IntPtr ctx, int index)
        {
            _ctx = ctx;
            _index = index;
            _id = Miniaudio.InputId(ctx, index);
            _name = Miniaudio.InputName(ctx, index);
            _channels = Math.Max(0, Miniaudio.InputChannels(ctx, index));
            _nativeRate = Math.Max(0, Miniaudio.InputNativeRate(ctx, index));
        }

        public string ID => _id;
        public string Name => _name;

        // LASP currently uses Layout[0] and SampleRates[0] in SoundIO backend; mirroring that
        public int   ChannelCount => _channels;
        public int[] SampleRates  => _nativeRate > 0 ? new[] { _nativeRate } : Array.Empty<int>();

        // Miniaudio has no "raw" flag like libsoundio; reporting false so LASP includes all devices
        public bool IsRaw => false;

        // Setting a conservative minimum so LASP chooses >= 1/60 s by default
        public double SoftwareLatencyMin => 1.0 / 60.0;

        public IInStream CreateInStream() => new Stream(this);

        // Miniaudio does not have a "close" method, so we do nothing (there are no per-device resources to free)
        public void Dispose() {  }

        internal IntPtr Ctx => _ctx;
        internal int    Index => _index;
    }

    internal sealed class Stream : IInStream
    {
        private readonly Device _device;

        // Native mu_device* managed by the wrapper.
        private IntPtr _nativeDevice;

        // Callback routing
        private static readonly Dictionary<IntPtr, Stream> sMap = new Dictionary<IntPtr, Stream>();
        private static readonly object sLock = new object();
        private static readonly Miniaudio.DeviceReadCallback sRead = OnReadThunk;

        // Live read window (valid only during callback)
        private IntPtr _readPtr;         // current byte* into interleaved buffer
        private int    _framesAvailable; // frames remaining in current callback
        private int    _bytesPerFrame;   // computed by native device
        private int    _lastBeginFrames; // frames handed out by latest BeginRead

        // A stable unmanaged ChannelArea (one item) that's reused across BeginRead/EndRead.
        private readonly unsafe IntPtr _areaPtr = Marshal.AllocHGlobal(sizeof(IInStream.ChannelArea));

        public Stream(Device device) { _device = device; }

        // ===== IInStream config (set by LASP before Open) =====
        public int    SampleRate      { get; set; }   // 0 = native
        public int    ChannelCount    { get; set; }   // 0 = native
        public double SoftwareLatency { get; set; }   // seconds
        public int  BytesPerFrame => _bytesPerFrame;
        public bool IsActive => _nativeDevice != IntPtr.Zero;

        private IntPtr _userData;
        public IntPtr UserData
        {
            get => _userData;
            set
            {
                _userData = value;
                if (_nativeDevice != IntPtr.Zero)
                {
                    Miniaudio.DeviceSetReadCallback(_nativeDevice, sRead, _userData);
                }
                // Register/refresh routing entry.
                if (_userData != IntPtr.Zero)
                {
                    lock (sLock) sMap[_userData] = this;
                }
            }
        }

        public void Open()
        {
            if (_nativeDevice != IntPtr.Zero) return;

            _nativeDevice = Miniaudio.DeviceCreate(
                _device.Ctx, _device.Index,
                SampleRate, ChannelCount, SoftwareLatency);

            if (_nativeDevice == IntPtr.Zero)
                throw new InvalidOperationException("Miniaudio: device initialization failed");

            // Set callback (user pointer may still be 0 here; it will be updated when UserData is set).
            Miniaudio.DeviceSetReadCallback(_nativeDevice, sRead, _userData);

            // Snapshot negotiated values (bytes-per-frame can be queried immediately).
            _bytesPerFrame = Miniaudio.DeviceBytesPerFrame(_nativeDevice);
            if (SampleRate == 0) SampleRate = Miniaudio.DeviceSampleRate(_nativeDevice);
            if (ChannelCount == 0 && _bytesPerFrame > 0)
            {
                // All capture is float32; bytesPerSample = 4.
                ChannelCount = _bytesPerFrame / 4;
            }
        }

        public void Start()
        {
            if (_nativeDevice == IntPtr.Zero) throw new InvalidOperationException("Stream not opened");
            Miniaudio.DeviceStart(_nativeDevice);
        }

        public void Stop()
        {
            if (_nativeDevice == IntPtr.Zero) return;

            // Remove routing before tearing down native resources.
            if (_userData != IntPtr.Zero)
            {
                lock (sLock) sMap.Remove(_userData);
            }

            Miniaudio.DeviceStop(_nativeDevice);
            Miniaudio.DeviceDestroy(_nativeDevice);
            _nativeDevice = IntPtr.Zero;
            _framesAvailable = 0;
            _lastBeginFrames = 0;
            _readPtr = IntPtr.Zero;
        }

        public void Dispose()
        {
            Stop();
            if (_areaPtr != IntPtr.Zero) Marshal.FreeHGlobal(_areaPtr);
        }

        // Callback bridge (native → managed)
        [PInvokeCallback(typeof(Miniaudio.DeviceReadCallback))]
        private static unsafe void OnReadThunk(IntPtr user, IntPtr interleaved, int frameCount, int bytesPerFrame)
        {
            Stream self;
            lock (sLock) sMap.TryGetValue(user, out self);
            if (self == null) return;

            self._readPtr = interleaved;
            self._framesAvailable = frameCount;
            self._bytesPerFrame = bytesPerFrame;

            var payload = new IInStream.InStreamData
            {
                Handle        = IntPtr.Zero, // not used in this backend
                UserData      = user,        // GCHandle to InputDeviceHandle (Lasp expects this)
                BytesPerFrame = bytesPerFrame
            };

            // min/left are not differentiated by miniaudio; supply frameCount for both.
            self.ReadCallback?.Invoke(ref payload, frameCount, frameCount);
        }

        // IInStream: BeginRead
        public unsafe void BeginRead(ref IInStream.InStreamData stream,
                                     out IInStream.ChannelArea* areas,
                                     ref int frameCount)
        {
            // If nothing is available in the current callback, return zero frames.
            if (_framesAvailable <= 0)
            {
                areas = (IInStream.ChannelArea*)_areaPtr;
                areas->Pointer = (byte*)IntPtr.Zero;
                areas->Step = _bytesPerFrame;
                frameCount = 0;
                _lastBeginFrames = 0;
                return;
            }

            int n = frameCount <= 0 ? _framesAvailable : Math.Min(frameCount, _framesAvailable);
            var area = (IInStream.ChannelArea*)_areaPtr;
            area->Pointer = (byte*)_readPtr;
            area->Step = _bytesPerFrame;

            areas = (IInStream.ChannelArea*)_areaPtr;
            frameCount = n;
            _lastBeginFrames = n;
        }

        // IInStream: EndRead
        public void EndRead(ref IInStream.InStreamData stream)
        {
            if (_lastBeginFrames > 0)
            {
                _readPtr = IntPtr.Add(_readPtr, _lastBeginFrames * _bytesPerFrame);
                _framesAvailable -= _lastBeginFrames;
                _lastBeginFrames = 0;
            }
        }

        // Expose handlers (InputDeviceHandle assigns these)
        public IInStream.ReadCallbackDelegate     ReadCallback     { get; set; }
        public IInStream.OverflowCallbackDelegate OverflowCallback { get; set; }
        public IInStream.ErrorCallbackDelegate    ErrorCallback    { get; set; }
    }
}
#endif // LASP_BACKEND_MINIAUDIO