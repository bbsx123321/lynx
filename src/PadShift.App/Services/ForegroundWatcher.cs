using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PadShift.App.Services;

/// <summary>
/// 轮询前台窗口所属进程名（1 Hz）。供「失焦自动停映射」使用：前台不再是关联游戏的进程时
/// 回调触发。只读状态、不捕获输入，开销可忽略。
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<string> _onForeground;
    private readonly Timer _timer;
    private bool _disposed;
    private string _lastName = "";
    private int _lastPid;

    public ForegroundWatcher(Action<string> onForeground)
    {
        _onForeground = onForeground;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void Poll()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId)
            {
                return;
            }

            string name;
            try
            {
                name = Process.GetProcessById((int)pid).ProcessName;
            }
            catch (Exception)
            {
                // 进程刚好退出；等下一个 tick。
                return;
            }

            lock (_gate)
            {
                if (_disposed || (pid == _lastPid && name == _lastName))
                {
                    return;
                }

                _lastPid = (int)pid;
                _lastName = name;
            }

            _onForeground(name);
        }
        catch (Exception)
        {
            // 诊断性轮询绝不能抛到线程池。
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
