using System;
using System.Linq;
using System.Threading;

namespace NzbDrone.Core.MediaFiles
{
    // Imports, scans and renames share folder-level protection. Fixed stripes avoid retaining deleted movie IDs.
    internal static class MovieFileOperationLock
    {
        private static readonly object[] Locks = Enumerable.Range(0, 256).Select(_ => new object()).ToArray();

        public static IDisposable Acquire(int movieId)
        {
            return new Lease(Locks[movieId & 255]);
        }

        private sealed class Lease : IDisposable
        {
            private object _sync;

            public Lease(object sync)
            {
                _sync = sync;
                Monitor.Enter(sync);
            }

            public void Dispose()
            {
                var sync = Interlocked.Exchange(ref _sync, null);
                if (sync != null)
                {
                    Monitor.Exit(sync);
                }
            }
        }
    }
}
