using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// The selected host inherits the original byte streams and owns its own
// admission record and shutdown. This launcher neither relays nor parses MCP.
internal sealed class InheritedStdioProcess(SafeProcessHandle process) : IDisposable
{
    public static InheritedStdioProcess Start(string executable, string workingDirectory)
    {
        using var input = DuplicateStandardHandle(-10);
        using var output = DuplicateStandardHandle(-11);
        using var error = DuplicateStandardHandle(-12);
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == 0) throw LastError();
        var attributes = Marshal.AllocHGlobal(checked((int)size));
        var handleList = IntPtr.Zero;
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw LastError();
            initialized = true;
            handleList = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handleList, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, output.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, 2 * IntPtr.Size, error.DangerousGetHandle());
            const int inheritHandleList = 0x20002;
            if (!UpdateProcThreadAttribute(attributes, 0, inheritHandleList, handleList, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw LastError();
            var startup = new StartupInfoEx
            {
                Attributes = attributes,
                Startup = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = input.DangerousGetHandle(), Output = output.DangerousGetHandle(), Error = error.DangerousGetHandle()
                }
            };
            const uint extendedStartupInfo = 0x80000;
            const uint createNoWindow = 0x08000000;
            var commandLine = new StringBuilder().Append('"').Append(executable).Append('"');
            if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, extendedStartupInfo | createNoWindow,
                    IntPtr.Zero, workingDirectory, ref startup, out var info)) throw LastError();
            using var thread = new SafeFileHandle(info.Thread, true);
            return new(new SafeProcessHandle(info.Process, true));
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(handleList);
            Marshal.FreeHGlobal(attributes);
        }
    }

    public int WaitForExitOrParentLoss(CancellationToken parentLost)
    {
        using var exit = new ProcessSignal(process);
        if (WaitHandle.WaitAny([exit, parentLost.WaitHandle]) != 0) return 0;
        if (!GetExitCodeProcess(process, out var code)) throw LastError();
        return unchecked((int)code);
    }

    public void Dispose() => process.Dispose();

    private sealed class ProcessSignal : WaitHandle
    {
        public ProcessSignal(SafeProcessHandle process) => SafeWaitHandle = new(process.DangerousGetHandle(), false);
    }

    private static SafeFileHandle DuplicateStandardHandle(int id)
    {
        var standard = GetStdHandle(id);
        if (standard == IntPtr.Zero || standard == new IntPtr(-1))
            throw new InvalidOperationException("The stable launcher requires inherited stdin, stdout and stderr.");
        var current = GetCurrentProcess();
        const uint sameAccess = 2;
        if (!DuplicateHandle(current, standard, current, out var duplicate, 0, true, sameAccess)) throw LastError();
        return duplicate;
    }

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, Fill, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute,
        IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processSecurity,
        IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
        string workingDirectory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
