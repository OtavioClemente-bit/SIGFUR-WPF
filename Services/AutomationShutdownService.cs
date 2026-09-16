using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SIGFUR.Wpf.Services;

public static class AutomationShutdownService
{
    private static readonly HashSet<string> AutomationProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge",
        "chrome",
        "msedgedriver",
        "chromedriver",
        "firefox",
        "geckodriver"
    };

    public static void CleanupCurrentProcessChildren()
    {
        if (!OperatingSystem.IsWindows()) return;
        var currentProcessId = Environment.ProcessId;
        var descendants = DescendantProcessIds(currentProcessId)
            .Where(processId => processId != currentProcessId)
            .OrderByDescending(processId => processId)
            .ToList();

        foreach (var processId in descendants)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!AutomationProcessNames.Contains(process.ProcessName)) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
            catch { }
        }
    }

    private static HashSet<int> DescendantProcessIds(int rootProcessId)
    {
        var result = new HashSet<int> { rootProcessId };
        if (rootProcessId <= 0) return result;

        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            var pairs = new List<(int ProcessId, int ParentProcessId)>();
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    pairs.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                }
                while (Process32Next(snapshot, ref entry));
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var pair in pairs)
                    if (result.Contains(pair.ParentProcessId) && result.Add(pair.ProcessId)) changed = true;
            }
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
