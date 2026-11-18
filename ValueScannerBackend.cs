using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MemoryEditor
{
    // Simple backend scanner that uses IGameClient to read memory in chunks and search for a byte pattern.
    public class ValueScannerBackend
    {
        private readonly IGameClient _client;

        public ValueScannerBackend(IGameClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<List<uint>> ScanRangeAsync(uint start, uint end, byte[] needle, int chunkSize = 0x1000, int concurrency = 6, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (needle == null || needle.Length == 0) throw new ArgumentException("needle");
            if (end <= start) return new List<uint>();

            var results = new List<uint>();
            var tasks = new List<Task<List<uint>>>();
            var sem = new SemaphoreSlim(concurrency);

            for (uint addr = start; addr < end; addr += (uint)chunkSize)
            {
                var size = (int)Math.Min(chunkSize, end - addr);
                await sem.WaitAsync(ct).ConfigureAwait(false);
                var chunkAddr = addr;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var found = new List<uint>();
                        var data = await _client.GetMemoryAsync(chunkAddr, (uint)size).ConfigureAwait(false);
                        if (data != null && data.Length > 0)
                        {
                            int idx = 0;
                            while ((idx = IndexOf(data, needle, idx)) >= 0)
                            {
                                found.Add(chunkAddr + (uint)idx);
                                idx = idx + 1; // allow overlapping
                            }
                        }
                        progress?.Report(found.Count);
                        return found;
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct));
            }

            var all = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var list in all)
                results.AddRange(list);

            results.Sort();
            return results;
        }

        // naive indexOf for byte arrays
        private static int IndexOf(byte[] haystack, byte[] needle, int startIndex)
        {
            if (needle.Length == 0) return startIndex;
            for (int i = startIndex; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }
}
