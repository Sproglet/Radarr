using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Movies
{
    // Adding a target cannot overtake an already authorized single-edition client submission.
    // Separate from the reentrant synchronous file-operation lock: submissions can await network IO.
    internal static class MovieEditionAcquisitionGate
    {
        private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

        public static IDisposable Acquire(int movieId)
        {
            var gate = Gates[movieId & 255];
            gate.Wait();
            return new Lease(gate);
        }

        public static async Task<IDisposable> AcquireAsync(int movieId)
        {
            var gate = Gates[movieId & 255];
            await gate.WaitAsync().ConfigureAwait(false);
            return new Lease(gate);
        }

        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim _gate;

            public Lease(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }
    }
}
