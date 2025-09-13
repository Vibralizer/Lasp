using System;
using System.Runtime.InteropServices;
using InvalidOp = System.InvalidOperationException;
using PInvokeCallbackAttribute = AOT.MonoPInvokeCallbackAttribute;
using Lasp.Backends;

namespace Lasp
{
    //
    // Internal input device handle class
    //
    // This is the one big monolithic class for managing a pair of an audio
    // input device and its input stream.
    //
    // Initially, it only manages a device object, then it starts streaming
    // when someone tries to read the audio data. The stream will be
    // automatically closed when the data hasn't been accessed for several
    // frames.
    //
    // It not only manages these objects, but also calculates audio levels of
    // each channel. They're managed in an on-demand fashion too: It starts
    // calculating them when accessed, and stops calculation when it's not
    // accessed.
    //
    sealed class InputDeviceHandle : IDisposable
    {
        #region Backend device object

        public IDevice BackendDevice => _device;
        public string ID => _device.ID;
        public bool IsValid => _device != null;

        IDevice _device;

        #endregion

        #region Backend stream object

        public bool IsStreamActive
          => _stream != null && _stream.IsActive;

        IInStream _stream;

        #endregion

        #region Basic stream properties

        public int StreamChannelCount => PreparedStream.ChannelCount;
        public int StreamSampleRate => PreparedStream.SampleRate;

        #endregion

        #region Per-channel audio levels

        public Unity.Mathematics.float4 GetChannelLevel(int channel)
          => Prepare() ? _audioLevels.GetLevel(channel) : 0;

        LevelMeter _audioLevels;

        #endregion

        #region Interleaved audio data

        public ReadOnlySpan<float> LastFrameWindow
          => MemoryMarshal.Cast<byte, float>(LastFrameWindowRaw);

        ReadOnlySpan<byte> LastFrameWindowRaw
          => Prepare() ?
             new ReadOnlySpan<byte>(_window, 0, _windowSize) :
             ReadOnlySpan<byte>.Empty;

        byte[] _window;
        int _windowSize;

        #endregion
        
        #region Mono-mixed audio data

        // Expose a mono view of the last-frame window (mixed from interleaved data).
        public ReadOnlySpan<float> LastFrameWindowMono
            => (_monoWindow != null && _monoWindowSize > 0)
                ? new ReadOnlySpan<float>(_monoWindow, 0, _monoWindowSize)
                : ReadOnlySpan<float>.Empty;

        float[] _monoWindow;
        int _monoWindowSize;

        // Mix interleaved N-channel -> mono (simple average). Stereo fast path.
        void UpdateMonoWindow(ReadOnlySpan<float> interleaved, int channels)
        {
            if (interleaved.Length == 0) { _monoWindowSize = 0; return; }

            int frames = interleaved.Length / channels;
            EnsureMonoCapacity(frames);

            if (channels == 1)
            {
                interleaved.CopyTo(_monoWindow.AsSpan(0, frames));
                _monoWindowSize = frames;
                return;
            }

            if (channels == 2)
            {
                int si = 0;
                for (int i = 0; i < frames; i++, si += 2)
                    _monoWindow[i] = 0.5f * (interleaved[si] + interleaved[si + 1]);
                _monoWindowSize = frames;
                return;
            }

            float inv = 1f / channels;
            int idx = 0;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++) sum += interleaved[idx++];
                _monoWindow[i] = sum * inv;
            }
            _monoWindowSize = frames;
        }

        void EnsureMonoCapacity(int frames)
        {
            if (_monoWindow == null || _monoWindow.Length < frames)
                _monoWindow = new float[frames];
        }

        #endregion

        
        #region "Prepare" method

        bool Prepare()
        {
            if (!IsValid) throw new InvalidOp("Invalid device");

            _sleepTimer = 0;

            if (IsStreamActive) return true;

            OpenStream();
            return false;
        }

        IInStream PreparedStream { get { Prepare(); return _stream; } }

        int _sleepTimer;

        #endregion

        #region Allocation/deallocation

        // Factory method
        public static InputDeviceHandle CreateAndOwn(IDevice device)
          => new InputDeviceHandle(device);

        // Private constructor
        InputDeviceHandle(IDevice device)
        {
            _self = GCHandle.Alloc(this);
            _device = device;
        }

        // IDisposable implementation
        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;

            _device?.Dispose();
            _device = null;

            if (_self.IsAllocated) _self.Free();
        }

        // A GC handle used to share 'this' pointer with unmanaged code
        GCHandle _self;

        #endregion

        #region State update methods

        public void Update(float deltaTime)
        {
            // Nothing to do when the stream is inactive.
            if (!IsStreamActive) return;

            // Close the stream when the sleep timer hits the threshold.
            if (++_sleepTimer > DelayToSleep)
            {
                CloseStream();
                return;
            }

            // Calculate the size of the last-frame window.
            _windowSize =
                Math.Min(_window.Length, CalculateBufferSize(deltaTime));

            lock (_ring)
            {
                // Copy the last frame data into the window buffer.
                if (_ring.FillCount >= _windowSize)
                    _ring.Read(new Span<byte>(_window, 0, _windowSize));
                else
                    _windowSize = 0; // Underflow

                // Reset the buffer when it detects an overflow.
                // TODO: Is this the best strategy to deal with overflow?
                if (_ring.OverflowCount > 0) _ring.Clear();
            }

            // Cast the just-copied bytes to float (interleaved).
            var windowFloats = MemoryMarshal.Cast<byte, float>
                (new ReadOnlySpan<byte>(_window, 0, _windowSize));

            // Mix down to mono once per frame.
            UpdateMonoWindow(windowFloats, _stream.ChannelCount);

            // Process the audio data.
            _audioLevels.ProcessAudioData(windowFloats);
        }

        const int DelayToSleep = 10;

        #endregion

        #region Stream initialization/finalization

        void OpenStream()
        {
            if (IsStreamActive)
                throw new InvalidOp("Stream already opened");

            try
            {
                _stream = _device.CreateInStream();

                // Calculate the best latency.
                // TODO: Should we use the target frame rate instead of 1/60?
                var bestLatency = Math.Max(1.0 / 60, _device.SoftwareLatencyMin);

                // Stream properties
                _stream.SoftwareLatency = bestLatency;
                _stream.ReadCallback = _readCallback;
                _stream.OverflowCallback = _overflowCallback;
                _stream.ErrorCallback = _errorCallback;
                _stream.UserData = GCHandle.ToIntPtr(_self);

                _stream.Open();

                // We want the buffers to meet the following requirements:
                // - Doesn't overflow if the main thread pauses for 4 frames.
                // - Doesn't overflow if the callback is invoked 4 times a frame.
                var latency = Math.Max(_stream.SoftwareLatency, bestLatency);
                var bufferSize = CalculateBufferSize((float)(latency * 4));

                // Ring/window buffer allocation
                _ring = new RingBuffer(bufferSize);
                _window = new byte[bufferSize];

                // Start streaming.
                _stream.Start();
            }
            catch
            {
                // Dispose the stream on an exception.
                _stream?.Dispose();
                _stream = null;
                throw;
            }

            _audioLevels = new LevelMeter(_stream.ChannelCount)
              { SampleRate = _stream.SampleRate };
        }

        void CloseStream()
        {
            if (!IsStreamActive)
                throw new InvalidOp("Stream not opened");

            _stream?.Dispose();
            _stream = null;
        }

        #endregion

        #region Private members

        // Input stream ring buffer
        // This object will be accessed from both the main/callback thread.
        // Must be locked when accessing it.
        RingBuffer _ring;

        // Calculate a buffer size based on a duration.
        int CalculateBufferSize(float second)
          => (int)(_stream.SampleRate * second) *
             _stream.ChannelCount * sizeof(float);

        #endregion

        #region IInStream callback delegates

        static IInStream.ReadCallbackDelegate
          _readCallback = OnReadInStream;

        static IInStream.OverflowCallbackDelegate
          _overflowCallback = OnOverflowInStream;

        static IInStream.ErrorCallbackDelegate
          _errorCallback = OnErrorInStream;

        [PInvokeCallback(typeof(IInStream.ReadCallbackDelegate))]
        unsafe static void OnReadInStream
          (ref IInStream.InStreamData stream, int min, int left)
        {
            // Recover the 'this' reference from the UserData pointer.
            var self = (InputDeviceHandle)
              GCHandle.FromIntPtr(stream.UserData).Target;

            while (left > 0)
            {
                // Start reading the buffer.
                var count = left;
                IInStream.ChannelArea* areas;
                self._stream.BeginRead(ref stream, out areas, ref count);

                // When getting count == 0, we must stop reading
                // immediately without calling InStream.EndRead.
                if (count == 0) break;

                if (areas == null)
                {
                    // We must do zero-fill when receiving a null pointer.
                    lock (self._ring)
                      self._ring.WriteEmpty(stream.BytesPerFrame * count);
                }
                else
                {
                    // Determine the memory span of the input data with
                    // assuming the data is tightly packed.
                    // TODO: Is this assumption always true?
                    var span = new ReadOnlySpan<Byte>
                      ((void*)areas[0].Pointer, areas[0].Step * count);

                    // Transfer the data to the ring buffer.
                    lock (self._ring) self._ring.Write(span);
                }

                self._stream.EndRead(ref stream);

                left -= count;
            }
        }

        [PInvokeCallback(typeof(IInStream.OverflowCallbackDelegate))]
        static void OnOverflowInStream(ref IInStream.InStreamData stream)
          => UnityEngine.Debug.LogWarning("InStream overflow");

        [PInvokeCallback(typeof(IInStream.ErrorCallbackDelegate))]
        static void OnErrorInStream
          (ref IInStream.InStreamData stream, int error)
          => UnityEngine.Debug.LogWarning($"InStream error ({error})");

        #endregion
    }
}
