using System.Windows.Threading;

namespace LlmWpfPrototype.Services;

public sealed partial class SolidWorksService
{
    private readonly SemaphoreSlim _cadCommandGate = new(1);
    private readonly Lazy<Task<Dispatcher>> _cadDispatcher = new(StartCadDispatcher);
    private string _lastActiveCadPath = string.Empty;

    private bool IsCadThread =>
        _cadDispatcher.IsValueCreated && _cadDispatcher.Value.IsCompletedSuccessfully &&
        _cadDispatcher.Value.Result.CheckAccess();

    private static Task<Dispatcher> StartCadDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }) { IsBackground = true, Name = "SolidWorks COM" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    internal async Task<T> RunCadCommandAsync<T>(Func<Task<T>> run, CancellationToken cancellationToken)
    {
        if (IsCadThread)
            return await run();

        await _cadCommandGate.WaitAsync(cancellationToken);
        try
        {
            var dispatcher = await _cadDispatcher.Value;
            return await dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    return await run();
                }
                finally
                {
                    _assemblyComponentIndex = null;
                }
            }, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
        }
        finally
        {
            _cadCommandGate.Release();
        }
    }

    private T ReadCadStatus<T>(Func<T> read, Func<T> cached)
    {
        if (IsCadThread) return read();
        if (!_cadCommandGate.Wait(0)) return cached();
        try
        {
            return _cadDispatcher.Value.GetAwaiter().GetResult().Invoke(read);
        }
        finally
        {
            _cadCommandGate.Release();
        }
    }

}
