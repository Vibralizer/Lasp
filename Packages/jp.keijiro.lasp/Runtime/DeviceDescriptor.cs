namespace Lasp
{
    //
    // A descriptor class that identifies a device (audio interface endpoint)
    // and describes its basic specifications.
    //
    public struct DeviceDescriptor
    {
        #region Property accessors

        public bool IsValid => _handle != null && _handle.IsValid;
        public string ID => _handle.BackendDevice.ID;
        public string Name => _handle.BackendDevice.Name;
        public int ChannelCount => _handle.BackendDevice.ChannelCount;
        public int SampleRate => _handle.BackendDevice.SampleRates[0];

        #endregion

        #region Internal members (initialized by DeviceManager)

        internal InputDeviceHandle _handle;

        #endregion
    }
}
