using System.Runtime.InteropServices;

namespace ZeroStrap.FastFlags.Memory
{
    // Resolves Nt* system-call numbers from ntdll's export table and emits raw
    // x64 syscall stubs into an RWX page. Lets us call NtWriteVirtualMemory /
    // NtReadVirtualMemory / NtProtectVirtualMemory without going through the
    // kernel32 shims that user-mode anti-cheat hooks sit on top of.
    //
    // SSNs are assigned by sorting every ntdll Nt*/Zw* export by its address:
    // the kernel ships syscall numbers in export order, so the index of a
    // sorted export list is the SSN. This holds even when the function's
    // prologue is overwritten by a hook — we never read the SSN out of the
    // prologue bytes.
    internal static class DirectSyscall
    {
        // Stub layout (11 bytes):
        //   4C 8B D1              mov r10, rcx
        //   B8 XX XX XX XX        mov eax, SSN
        //   0F 05                 syscall
        //   C3                    ret
        private const int StubSize = 11;
        private const int StubPageSize = 4096;

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);

        private static readonly object _lock = new();
        private static bool _ready;
        private static IntPtr _stubPage;
        private static int _nextStubOffset;
        private static readonly Dictionary<string, uint> _ssnCache = new(StringComparer.Ordinal);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int NtWriteVirtualMemoryDelegate(
            IntPtr hProcess, IntPtr baseAddr, IntPtr buffer, UIntPtr size, out UIntPtr written);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int NtReadVirtualMemoryDelegate(
            IntPtr hProcess, IntPtr baseAddr, IntPtr buffer, UIntPtr size, out UIntPtr read);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int NtProtectVirtualMemoryDelegate(
            IntPtr hProcess, ref IntPtr baseAddr, ref UIntPtr size, uint newProtect, out uint oldProtect);

        private static NtWriteVirtualMemoryDelegate? _write;
        private static NtReadVirtualMemoryDelegate? _read;
        private static NtProtectVirtualMemoryDelegate? _protect;

        public static NtWriteVirtualMemoryDelegate NtWriteVirtualMemory =>
            _write ??= Marshal.GetDelegateForFunctionPointer<NtWriteVirtualMemoryDelegate>(
                GetStub("NtWriteVirtualMemory"));

        public static NtReadVirtualMemoryDelegate NtReadVirtualMemory =>
            _read ??= Marshal.GetDelegateForFunctionPointer<NtReadVirtualMemoryDelegate>(
                GetStub("NtReadVirtualMemory"));

        public static NtProtectVirtualMemoryDelegate NtProtectVirtualMemory =>
            _protect ??= Marshal.GetDelegateForFunctionPointer<NtProtectVirtualMemoryDelegate>(
                GetStub("NtProtectVirtualMemory"));

        public static IntPtr GetStub(string ntFunctionName)
        {
            EnsureReady();

            if (!_ssnCache.TryGetValue(ntFunctionName, out uint ssn))
                throw new InvalidOperationException($"SSN not resolved for {ntFunctionName}");

            int offset;
            lock (_lock)
            {
                if (_nextStubOffset + StubSize > StubPageSize)
                    throw new InvalidOperationException("syscall stub page exhausted");
                offset = _nextStubOffset;
                _nextStubOffset += StubSize;
            }

            IntPtr stub = IntPtr.Add(_stubPage, offset);
            unsafe
            {
                byte* p = (byte*)stub;
                p[0] = 0x4C; p[1] = 0x8B; p[2] = 0xD1;                      // mov r10, rcx
                p[3] = 0xB8;                                                 // mov eax, imm32
                p[4] = (byte)(ssn & 0xFF);
                p[5] = (byte)((ssn >> 8) & 0xFF);
                p[6] = (byte)((ssn >> 16) & 0xFF);
                p[7] = (byte)((ssn >> 24) & 0xFF);
                p[8] = 0x0F; p[9] = 0x05;                                   // syscall
                p[10] = 0xC3;                                                // ret
            }
            return stub;
        }

        private static void EnsureReady()
        {
            if (_ready) return;
            lock (_lock)
            {
                if (_ready) return;

                _stubPage = VirtualAlloc(IntPtr.Zero, (UIntPtr)StubPageSize,
                    MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                if (_stubPage == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"VirtualAlloc failed for syscall stub page (err {Marshal.GetLastWin32Error()})");

                ResolveSsnsBySortedExports();
                _ready = true;
            }
        }

        // Walk ntdll's PE export table, keep Nt*/Zw* exports, sort them by virtual
        // address, assign SSNs by index. Nt* and Zw* share the same SSN (both
        // target the same system service), so dedup on target address.
        private static unsafe void ResolveSsnsBySortedExports()
        {
            IntPtr ntdll = LoadLibraryW("ntdll.dll");
            if (ntdll == IntPtr.Zero)
                throw new InvalidOperationException("ntdll.dll not loadable");

            byte* dos = (byte*)ntdll;
            int ntHeaderOffset = *(int*)(dos + 0x3C);
            byte* nt = dos + ntHeaderOffset;

            // IMAGE_NT_HEADERS64:
            //   +0x18 OptionalHeader
            // IMAGE_OPTIONAL_HEADER64:
            //   +0x70 DataDirectory[0]  (export directory, RVA then size)
            uint exportRva = *(uint*)(nt + 0x18 + 0x70);
            if (exportRva == 0)
                throw new InvalidOperationException("ntdll missing export directory");

            byte* exp = dos + exportRva;
            uint numNames = *(uint*)(exp + 0x18);
            uint funcsRva = *(uint*)(exp + 0x1C);
            uint namesRva = *(uint*)(exp + 0x20);
            uint ordsRva  = *(uint*)(exp + 0x24);

            uint* funcs  = (uint*)(dos + funcsRva);
            uint* names  = (uint*)(dos + namesRva);
            ushort* ords = (ushort*)(dos + ordsRva);

            var exports = new List<(string Name, ulong Addr)>(2048);
            for (uint i = 0; i < numNames; i++)
            {
                sbyte* namePtr = (sbyte*)(dos + names[i]);
                string n = new string(namePtr);
                if (n.Length < 3)
                    continue;
                if (n[0] != 'N' && n[0] != 'Z')
                    continue;
                bool isNt = n[0] == 'N' && n[1] == 't';
                bool isZw = n[0] == 'Z' && n[1] == 'w';
                if (!isNt && !isZw)
                    continue;
                if (!char.IsUpper(n[2]))
                    continue;

                ushort ord = ords[i];
                ulong addr = (ulong)(dos + funcs[ord]);
                exports.Add((n, addr));
            }

            exports.Sort((a, b) => a.Addr.CompareTo(b.Addr));

            var ssnByAddr = new Dictionary<ulong, uint>();
            uint next = 0;
            foreach (var (name, addr) in exports)
            {
                if (!ssnByAddr.TryGetValue(addr, out uint ssn))
                {
                    ssn = next++;
                    ssnByAddr[addr] = ssn;
                }
                _ssnCache[name] = ssn;
            }
        }
    }
}
