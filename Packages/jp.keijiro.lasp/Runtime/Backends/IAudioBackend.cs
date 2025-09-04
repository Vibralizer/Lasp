using System;

namespace Lasp.Backends
{
    internal interface IAudioBackend
    {
        IContext CreateContext();
    }

    internal interface IContext : IDisposable
    {
        public delegate void OnDevicesChangeDelegate(IntPtr pointer);
        OnDevicesChangeDelegate OnDevicesChange { get; set; }

        // Same cadence as AudioSystem: Connect once, FlushEvents every EarlyUpdate.
        void Connect();
        void FlushEvents();

        int InputDeviceCount { get; }
        int DefaultInputDeviceIndex { get; }

        IDevice GetInputDevice(int index);
    }

    internal interface IDevice : IDisposable
    {
        string ID { get; }
        string Name { get; }
        int ChannelCount { get; }
        int[] SampleRates { get; }
        bool IsRaw { get; } // LASP filters “raw” devices out
        double SoftwareLatencyMin { get; }

        IInStream CreateInStream();
    }

    internal interface IInStream : IDisposable
    {
        // Set before Open(); LASP currently sets Format=Float32, Layout=first layout,
        // and chooses latency from device.SoftwareLatencyMin (>= 1/60) before Open().
        int SampleRate { get; set; }  // 0 = device native (SoundIO default)
        int ChannelCount { get; set; }  // 0 = device native (SoundIO default)
        double SoftwareLatency { get; set; }  // seconds

        int  BytesPerFrame { get; }
        bool IsActive { get; }
        IntPtr UserData { get; set; } // GCHandle to InputDeviceHandle; LASP recovers it in the callback.

        // Callbacks (same flow LASP uses today).
        public delegate void ReadCallbackDelegate(ref InStreamData stream, int min, int left);
        public delegate void OverflowCallbackDelegate(ref InStreamData stream);
        public delegate void ErrorCallbackDelegate(ref InStreamData stream, int error);

        ReadCallbackDelegate      ReadCallback     { get; set; }
        OverflowCallbackDelegate  OverflowCallback { get; set; }
        ErrorCallbackDelegate     ErrorCallback    { get; set; }

        void Open();
        void Start();
        void Stop();

        // ChannelArea matches SoundIO layout used by LASP: interleaved, Step = bytesPerFrame.
        public unsafe struct ChannelArea { public byte* Pointer; public int Step; }

        // Opaque carrier passed into callbacks; mirrors what LASP expects today:
        // UserData is a GCHandle to InputDeviceHandle, BytesPerFrame read inside the loop.
        public struct InStreamData
        {
            internal IntPtr Handle;   // backend specific
            public   IntPtr UserData; // GCHandle to InputDeviceHandle
            public   int    BytesPerFrame;
        }

        // Backend performs the actual begin/end read using 'Handle'.
        public unsafe void BeginRead(ref InStreamData stream, out ChannelArea* areas, ref int frameCount);
        public void EndRead(ref InStreamData stream);
    }
}
