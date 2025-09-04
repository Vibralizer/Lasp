using System;
using System.Collections.Generic;
using PInvokeCallbackAttribute = AOT.MonoPInvokeCallbackAttribute;

namespace Lasp.Backends
{
    internal sealed class SoundIOBackend : IAudioBackend
    {
        public IContext CreateContext() => new Context();
    }

    internal sealed class Context : IContext
    {
        private SoundIO.Context _context;

        // Keep the native thunk alive for the lifetime of the context.
        private SoundIO.Context.OnDevicesChangeDelegate _nativeDevicesChangedThunk;

        // Store the managed delegate the app sets (root it here so GC can’t collect it).
        private IContext.OnDevicesChangeDelegate _managedDevicesChanged;

        public Context()
        {
            _context = SoundIO.Context.Create();

            // Keep this thunk rooted for the entire context lifetime (AOT-safe).
            _nativeDevicesChangedThunk = OnDevicesChangeThunk;
            _context.OnDevicesChange = _nativeDevicesChangedThunk;
        }
        
        public IContext.OnDevicesChangeDelegate OnDevicesChange
        {
            get => _managedDevicesChanged;
            set => _managedDevicesChanged = value;
        }

        // Libsoundio → our managed delegate
        [PInvokeCallback(typeof(SoundIO.Context.OnDevicesChangeDelegate))]
        private void OnDevicesChangeThunk(IntPtr _)
        {
            _managedDevicesChanged?.Invoke(IntPtr.Zero);
        }

        public void Connect() => _context.Connect();
        public void FlushEvents() => _context.FlushEvents();

        public int InputDeviceCount => _context.InputDeviceCount;
        public int DefaultInputDeviceIndex => _context.DefaultInputDeviceIndex;
        public IDevice GetInputDevice(int index) => new Device(_context.GetInputDevice(index));

        public void Dispose()
        {
            if (_context != null) _context.OnDevicesChange = null;
            _context?.Dispose();
            _context = null;
            _managedDevicesChanged = null;
            _nativeDevicesChangedThunk = null;
        }
    }

    internal sealed class Device : IDevice
    {
        readonly SoundIO.Device _device;
        public Device(SoundIO.Device device) { _device = device; }

        public string ID   => _device.ID;
        public string Name => _device.Name;

        public int   ChannelCount => _device.Layouts.Length > 0 ? _device.Layouts[0].ChannelCount : 0;
        public int[] SampleRates // LASP reads SampleRates[0]
        {
            get
            {
                var s = _device.SampleRates; // Span-like; has Length + indexer
                if (s.Length == 0) return Array.Empty<int>();
                var arr = new int[s.Length];
                for (int i = 0; i < s.Length; i++)  // compatible with older Unity C# as well
                    arr[i] = s[i];
                return arr;
            }
        }
        public bool IsRaw => _device.IsRaw; // LASP filters raw devices out
        public double SoftwareLatencyMin => _device.SoftwareLatencyMin;
        public IInStream CreateInStream() => new Stream(this);

        public void Dispose() { _device?.Dispose(); }
        internal SoundIO.Device Raw => _device;
    }

    internal sealed class Stream : IInStream
    {
        readonly Device _device;
        SoundIO.InStream _sio;
        bool _started;

        public Stream(Device device) { _device = device; }

        // Config LASP settings before calling Open()
        public int    SampleRate      { get; set; }  // ignored by LASP currently (uses device native)
        public int    ChannelCount    { get; set; }  // ignored if 0; we set layout from device[0]
        public double SoftwareLatency { get; set; }

        public int BytesPerFrame => _sio?.BytesPerFrame ?? 0;
        public bool IsActive     => _sio != null && !_sio.IsInvalid && !_sio.IsClosed;
        public IntPtr UserData   { get; set; } // GCHandle to InputDeviceHandle (Lasp recovers it).

        // Keep delegates alive to be AOT-safe (rather than allocating on every Open())
        static readonly SoundIO.InStream.ReadCallbackDelegate     sRead  = ReadThunk;
        static readonly SoundIO.InStream.OverflowCallbackDelegate sOver  = OverThunk;
        static readonly SoundIO.InStream.ErrorCallbackDelegate    sErr   = ErrThunk;

        // Map InputDeviceHandle GCHandle (UserData) -> Stream, so we can find 'this' in the thunk.
        static readonly Dictionary<IntPtr, Stream> sMap = new Dictionary<IntPtr, Stream>();

        public void Open()
        {
            if (_sio != null) return;

            _sio = SoundIO.InStream.Create(_device.Raw);
            if (_sio.IsInvalid) throw new InvalidOperationException("SoundIO.InStream allocation error");
            
            if (_device.Raw.Layouts.Length == 0) throw new InvalidOperationException("No channel layout");
            _sio.Format           = SoundIO.Format.Float32LE;
            _sio.Layout           = _device.Raw.Layouts[0];
            _sio.SoftwareLatency  = SoftwareLatency;
            _sio.ReadCallback     = sRead;
            _sio.OverflowCallback = sOver;
            _sio.ErrorCallback    = sErr;

            var err = _sio.Open();
            if (err != SoundIO.Error.None)
                throw new InvalidOperationException($"Stream initialization error ({err})");
            
            // Now that Open() succeeded, register the instance for callback routing.
            _sio.UserData = UserData;
            lock (sMap) sMap[UserData] = this;
            
            // Capture a negotiated format so LASP can size its buffers correctly.
            SampleRate   = _sio.SampleRate;
            ChannelCount = _sio.Layout.ChannelCount;
        }

        public void Start()
        {
            if (_sio == null) throw new InvalidOperationException("Stream not opened");
            if (_started) return;
            _sio.Start();
            _started = true;
        }

        public void Stop()
        {
            if (_sio == null) return;
            _sio.Dispose(); // SoundIO closes on dispose; LASP does this on stop/close.
            _sio = null;
            _started = false;
            lock (sMap) sMap.Remove(UserData);
        }

        public void Dispose() => Stop();

        // ===== Callback bridge (native → managed) ================================

        [PInvokeCallback(typeof(SoundIO.InStream.ReadCallbackDelegate))]
        static unsafe void ReadThunk(ref SoundIO.InStreamData s, int min, int left)
        {
            if (!TryGetSelf(s.UserData, out var self)) return;

            // Pin the ref struct and take its address inside 'fixed' so GC doesn't move it while we use the pointer.
            fixed (SoundIO.InStreamData* ps = &s)
            {
                var payload = new IInStream.InStreamData {
                    Handle        = (IntPtr)ps, // pointer to the original struct
                    UserData      = s.UserData,
                    BytesPerFrame = s.BytesPerFrame
                };
                self.ReadCallback?.Invoke(ref payload, min, left);
            }
        }

        [PInvokeCallback(typeof(SoundIO.InStream.OverflowCallbackDelegate))]
        static void OverThunk(ref SoundIO.InStreamData s)
        {
            if (!TryGetSelf(s.UserData, out var self)) return;
            var payload = new IInStream.InStreamData {
                Handle        = IntPtr.Zero,
                UserData      = s.UserData,
                BytesPerFrame = s.BytesPerFrame
            };
            self.OverflowCallback?.Invoke(ref payload);
        }

        [PInvokeCallback(typeof(SoundIO.InStream.ErrorCallbackDelegate))]
        static void ErrThunk(ref SoundIO.InStreamData s, SoundIO.Error err)
        {
            if (!TryGetSelf(s.UserData, out var self)) return;
            var payload = new IInStream.InStreamData {
                Handle        = IntPtr.Zero,
                UserData      = s.UserData,
                BytesPerFrame = s.BytesPerFrame
            };
            self.ErrorCallback?.Invoke(ref payload, (int)err);
        }

        static bool TryGetSelf(IntPtr key, out Stream self)
        {
            lock (sMap) return sMap.TryGetValue(key, out self);
        }

        // ===== IInStream: BeginRead / EndRead (identical calling pattern) ========

        public unsafe void BeginRead(ref IInStream.InStreamData stream,
            out IInStream.ChannelArea* areas,
            ref int frameCount)
        {
            var sio = (SoundIO.InStreamData*)stream.Handle;
            SoundIO.ChannelArea* nativeAreas;
            sio->BeginRead(out nativeAreas, ref frameCount);
            areas = (IInStream.ChannelArea*)nativeAreas;
        }

        public unsafe void EndRead(ref IInStream.InStreamData stream)
        {
            var sio = (SoundIO.InStreamData*)stream.Handle;
            sio->EndRead();
        }

        // Expose the handlers (InputDeviceHandle assigns these).
        public IInStream.ReadCallbackDelegate     ReadCallback     { get; set; }
        public IInStream.OverflowCallbackDelegate OverflowCallback { get; set; }
        public IInStream.ErrorCallbackDelegate    ErrorCallback    { get; set; }
    }
}
