using System.Runtime.InteropServices;

namespace ZeroStrap.FastFlags.Memory
{
    // Opens the Roblox process with a Hyperion-safe access mask and exposes
    // typed read/write/protect primitives backed by direct syscall stubs.
    //
    // The Roblox client (hyperion) blocks OpenProcess calls whose desired
    // access mask includes PROCESS_QUERY_INFORMATION (0x0400). The pure
    // VM-triplet (VM_OPERATION | VM_READ | VM_WRITE = 0x38) is left alone,
    // which is enough for us: we never need to query process info — the pid
    // and module base come from EnumProcessModulesEx, which operates on a
    // handle we already own.
    internal sealed class ProcessMemoryWriter : IDisposable
    {
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_READ      = 0x0010;
        private const uint PROCESS_VM_WRITE     = 0x0020;
        private const uint PROCESS_HYPERION_SAFE = PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE;

        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint LIST_MODULES_64BIT = 0x02;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(
            IntPtr hProcess, [Out] IntPtr[] modules, uint cb, out uint needed, uint filter);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameExW(
            IntPtr hProcess, IntPtr hModule, StringBuilder name, int size);

        public IntPtr Handle { get; private set; }
        public ulong BaseAddress { get; private set; }
        public int Pid { get; }

        public ProcessMemoryWriter(int pid) { Pid = pid; }

        public bool Attach(TimeSpan timeout)
        {
            const string LOG = "ProcessMemoryWriter::Attach";
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                IntPtr h = OpenProcess(PROCESS_HYPERION_SAFE, false, Pid);
                if (h != IntPtr.Zero)
                {
                    Handle = h;
                    if (TryResolveBase())
                    {
                        App.Logger.WriteLine(LOG, $"attached pid={Pid} base=0x{BaseAddress:X}");
                        return true;
                    }
                    CloseHandle(h);
                    Handle = IntPtr.Zero;
                }
                Thread.Sleep(500);
            }
            App.Logger.WriteLine(LOG, $"timed out attaching to pid={Pid}");
            return false;
        }

        private bool TryResolveBase()
        {
            var modules = new IntPtr[1024];
            uint cb = (uint)(modules.Length * IntPtr.Size);
            if (!EnumProcessModulesEx(Handle, modules, cb, out uint needed, LIST_MODULES_64BIT))
                return false;

            int count = (int)Math.Min(needed / (uint)IntPtr.Size, (uint)modules.Length);
            var sb = new StringBuilder(512);

            for (int i = 0; i < count; i++)
            {
                sb.Clear();
                if (GetModuleFileNameExW(Handle, modules[i], sb, sb.Capacity) == 0)
                    continue;

                string path = sb.ToString();
                if (path.EndsWith("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("RobloxStudioBeta.exe", StringComparison.OrdinalIgnoreCase))
                {
                    BaseAddress = (ulong)modules[i].ToInt64();
                    return true;
                }
            }
            return false;
        }

        public unsafe bool WriteBytes(ulong absoluteAddr, ReadOnlySpan<byte> data)
        {
            if (Handle == IntPtr.Zero)
                return false;

            fixed (byte* p = data)
            {
                int status = DirectSyscall.NtWriteVirtualMemory(
                    Handle, (IntPtr)(long)absoluteAddr, (IntPtr)p, (UIntPtr)data.Length, out _);
                return status >= 0;
            }
        }

        public unsafe bool ReadBytes(ulong absoluteAddr, Span<byte> dest)
        {
            if (Handle == IntPtr.Zero)
                return false;

            fixed (byte* p = dest)
            {
                int status = DirectSyscall.NtReadVirtualMemory(
                    Handle, (IntPtr)(long)absoluteAddr, (IntPtr)p, (UIntPtr)dest.Length, out _);
                return status >= 0;
            }
        }

        public bool EnsureWritable(ulong absoluteAddr, int size)
        {
            if (Handle == IntPtr.Zero)
                return false;

            IntPtr addr = (IntPtr)(long)absoluteAddr;
            UIntPtr len = (UIntPtr)size;
            int status = DirectSyscall.NtProtectVirtualMemory(
                Handle, ref addr, ref len, PAGE_EXECUTE_READWRITE, out _);
            return status >= 0;
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                CloseHandle(Handle);
                Handle = IntPtr.Zero;
            }
        }
    }
}
