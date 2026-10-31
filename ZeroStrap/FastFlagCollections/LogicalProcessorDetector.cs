using Windows.Win32;

namespace ZeroStrap.FastFlagCollections
{
    public static class LogicalProcessorDetector
    {
        private const ushort AllProcessorGroups = 0xFFFF;

        /// <summary>
        /// Count is only returned when .NET and Windows agree; otherwise the UI asks the user to enter it.
        /// </summary>
        public static (int? Count, bool Reliable) Detect()
        {
            try
            {
                return Decide(Environment.ProcessorCount, (int)PInvoke.GetActiveProcessorCount(AllProcessorGroups));
            }
            catch
            {
                return (null, false);
            }
        }

        internal static (int? Count, bool Reliable) Decide(int environmentCount, int nativeCount) =>
            environmentCount > 0 && environmentCount == nativeCount ? (environmentCount, true) : (null, false);
    }
}
