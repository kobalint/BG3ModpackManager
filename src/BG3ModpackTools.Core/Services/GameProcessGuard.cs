using System.Diagnostics;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Represents a running Baldur's Gate 3 executable instance.
/// </summary>
public sealed record RunningGameProcess(int Pid, string ProcessName, string VariantLabel);

/// <summary>
/// Thrown when a file-mutating mod operation is attempted while Baldur's Gate 3 is running.
/// </summary>
public sealed class GameRunningException : InvalidOperationException
{
    public IReadOnlyList<RunningGameProcess> RunningProcesses { get; }

    public GameRunningException(string operationDescription, IReadOnlyList<RunningGameProcess> runningProcesses)
        : base($"Cannot perform '{operationDescription}' while Baldur's Gate 3 is running ({string.Join(", ", runningProcesses.Select(p => $"{p.VariantLabel} [PID {p.Pid}]"))}). Please close the game first.")
    {
        RunningProcesses = runningProcesses;
    }
}

/// <summary>
/// Detects running Baldur's Gate 3 processes (Vulkan bg3.exe and DirectX 11 bg3_dx11.exe)
/// and blocks mod file moves until the game is closed.
/// </summary>
public static class GameProcessGuard
{
    private static Func<IReadOnlyList<RunningGameProcess>> _provider = QueryLiveProcesses;

    private sealed class TestOverrideScope : IDisposable
    {
        private readonly Func<IReadOnlyList<RunningGameProcess>> _previous;
        private bool _disposed;

        public TestOverrideScope(Func<IReadOnlyList<RunningGameProcess>> previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _provider = _previous;
                _disposed = true;
            }
        }
    }

    public static IDisposable OverrideForTests(Func<IReadOnlyList<RunningGameProcess>> provider)
    {
        Func<IReadOnlyList<RunningGameProcess>> prev = _provider;
        _provider = provider;
        return new TestOverrideScope(prev);
    }

    public static IReadOnlyList<RunningGameProcess> GetRunningGameProcesses()
    {
        return _provider();
    }

    public static bool IsGameRunning()
    {
        return GetRunningGameProcesses().Count > 0;
    }

    public static void EnsureGameNotRunning(string operationDescription)
    {
        IReadOnlyList<RunningGameProcess> running = GetRunningGameProcesses();
        if (running.Count > 0)
        {
            throw new GameRunningException(operationDescription, running);
        }
    }

    /// <summary>
    /// Requests graceful close via WM_CLOSE on all running BG3 windows and polls until exited or timed out.
    /// </summary>
    public static async Task<bool> TryRequestGracefulCloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RunningGameProcess> running = GetRunningGameProcesses();
        if (running.Count == 0)
        {
            return true;
        }

        foreach (RunningGameProcess info in running)
        {
            try
            {
                using Process proc = Process.GetProcessById(info.Pid);
                if (!proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                {
                    proc.CloseMainWindow();
                }
            }
            catch
            {
                // Ignore processes that already exited or deny access.
            }
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsGameRunning())
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return !IsGameRunning();
    }

    private static IReadOnlyList<RunningGameProcess> QueryLiveProcesses()
    {
        var list = new List<RunningGameProcess>();
        AddByName(list, "bg3", "Baldur's Gate 3 (Vulkan - bg3.exe)");
        AddByName(list, "bg3_dx11", "Baldur's Gate 3 (DirectX 11 - bg3_dx11.exe)");
        return list;
    }

    private static void AddByName(List<RunningGameProcess> list, string processName, string label)
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!p.HasExited)
                    {
                        list.Add(new RunningGameProcess(p.Id, p.ProcessName, label));
                    }
                }
                catch
                {
                    list.Add(new RunningGameProcess(p.Id, processName, label));
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            // Ignore transient process enumeration failures.
        }
    }
}
