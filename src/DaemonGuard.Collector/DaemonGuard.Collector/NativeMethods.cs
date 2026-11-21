using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DaemonGuard.Collector;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessIoCounters(SafeProcessHandle processHandle, out IoCounters ioCounters);
}
