using System.Runtime.InteropServices;

namespace CATIA_CFD_Clean;

/// <summary>
/// Global low-level F2 hook that intercepts plain F2 (no modifiers) only while CATIA is in the
/// foreground and <see cref="Armed"/> is set. Otherwise the key passes through untouched.
/// </summary>
/// <remarks>
/// The hook lives on its own thread with its own message loop. Low-level hook callbacks are
/// delivered to the installing thread, so installing on the UI thread would stall every
/// keystroke system-wide whenever the UI is busy in a slow CATIA call, and Windows silently
/// removes hooks that exceed its timeout. The callback makes no COM calls and does no I/O.
/// </remarks>
internal sealed class CatiaF2Hook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_QUIT = 0x0012;
    private const int VK_F2 = 0x71;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // Kept in a field so the GC cannot collect the delegate while the hook is installed.
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action _onPressed;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    // Hook-thread-only state.
    private bool _f2Down;       // F2 is physically held (ignore auto-repeat)
    private bool _swallowing;   // the current F2 press was swallowed; swallow its key-up too

    /// <summary>When false, F2 always passes through. Set by the owner from its selection poll.</summary>
    public volatile bool Armed;

    /// <summary>Process id of the CATIA instance whose foreground windows F2 is intercepted for; 0 = none.</summary>
    public volatile int CatiaProcessId;

    /// <param name="onPressed">Called on the hook thread for each intercepted F2 press. Must return quickly.</param>
    public CatiaF2Hook(Action onPressed)
    {
        _onPressed = onPressed;
        _proc = Callback;
    }

    /// <summary>Starts the hook thread. Returns 0 on success, otherwise the Win32 error code.</summary>
    public int Start()
    {
        if (_thread is not null)
            return 0;

        int error = 0;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                error = Marshal.GetLastWin32Error();
            ready.Set();
            if (_hook == IntPtr.Zero)
                return;

            try
            {
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
                {
                    // No windows on this thread; the loop only exists to deliver hook callbacks.
                }
            }
            finally
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        })
        {
            IsBackground = true,
            Name = "CATIA F2 hook",
        };
        _thread.Start();
        ready.Wait();

        if (error != 0)
            _thread = null;
        return error;
    }

    public void Dispose()
    {
        Thread? thread = _thread;
        if (thread is null)
            return;

        _thread = null;
        Armed = false;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(1000);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // lParam points to a KBDLLHOOKSTRUCT whose first field is vkCode.
            if (nCode >= 0 && Marshal.ReadInt32(lParam) == VK_F2)
            {
                int msg = (int)wParam;
                if (msg == WM_KEYDOWN)
                {
                    if (_f2Down)
                    {
                        // Auto-repeat while held: swallow if the first press was, never re-fire.
                        if (_swallowing)
                            return (IntPtr)1;
                    }
                    else
                    {
                        _f2Down = true;
                        _swallowing = ShouldIntercept();
                        if (_swallowing)
                        {
                            _onPressed();
                            return (IntPtr)1;
                        }
                    }
                }
                else if (msg == WM_KEYUP)
                {
                    bool swallow = _swallowing;
                    _f2Down = false;
                    _swallowing = false;
                    if (swallow)
                        return (IntPtr)1;
                }
            }
        }
        catch
        {
            // Never let an exception escape into the OS; fall through to default handling.
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool ShouldIntercept()
    {
        int catiaPid = CatiaProcessId;
        if (!Armed || catiaPid == 0 || ModifierDown())
            return false;

        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)catiaPid;
    }

    private static bool ModifierDown() =>
        IsDown(VK_SHIFT) || IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN);

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Returns the process id of the foreground window if it is CATIA V5 (CNEXT.exe), else 0.
    /// Matches by process name only: window titles are unreliable (e.g. an editor with a
    /// "CATIA_..." project open would otherwise match).
    /// </summary>
    public static int GetForegroundCatiaProcessId(ref (int Pid, bool IsCatia) cache)
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return 0;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
            return 0;

        if (cache.Pid != (int)pid)
        {
            bool isCatia = false;
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                isCatia = proc.ProcessName.Equals("CNEXT", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Process exited or is inaccessible: not CATIA as far as we can tell.
            }
            cache = ((int)pid, isCatia);
        }
        return cache.IsCatia ? cache.Pid : 0;
    }
}
