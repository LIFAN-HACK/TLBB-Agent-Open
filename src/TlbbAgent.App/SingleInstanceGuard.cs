using System.Threading;

namespace TlbbAgent.App;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static SingleInstanceGuard Acquire()
    {
        var mutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\LongBanXing.Product.SingleInstance.v1",
            createdNew: out var createdNew);
        return new SingleInstanceGuard(mutex, createdNew);
    }

    public bool IsPrimaryInstance => _ownsMutex;

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process is already shutting down; disposing the handle is sufficient.
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
