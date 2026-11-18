using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryEditor;

/// <summary>
/// Xbox 360 debug monitor (XBDM) client.
///
/// Speaks the real XBDM text protocol on TCP/730:
///   200 = OK (single line)      201 = connected banner
///   202 = multi-line follows    203 = binary follows
///   4xx = error
///
/// All commands are serialized over a single socket so concurrent callers
/// (watch list timers, scripts, scanners) can never interleave responses.
/// JRPC2 is detected as an XBDM plugin (it registers the "consolefeatures" command).
/// </summary>
public class XBDMClient : IDisposable, IGameClient
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly string _ipAddress;
    private readonly int _port;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // simple receive buffer for line/binary reads
    private readonly byte[] _rbuf = new byte[1 << 16];
    private int _rpos;
    private int _rlen;

    private bool? _getmem2Supported;
    private bool _connected;

    public const int DefaultTimeoutMs = 5000;

    public bool IsConnected => _connected && (_tcp?.Connected ?? false);
    public bool IsXBDMConnected => IsConnected;
    public bool IsJRPCConnected { get; private set; }
    public string IpAddress => _ipAddress;
    public int Port => _port;

    /// <summary>When true every memory operation is logged (noisy).</summary>
    public bool Verbose { get; set; }

    /// <summary>Total bytes read since connect (for status display).</summary>
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }
    public double LastLatencyMs { get; private set; }

    public event EventHandler<string>? MessageReceived;
    public event EventHandler? ConnectionStatusChanged;

    public XBDMClient(string ipAddress, int port = 730)
    {
        _ipAddress = ipAddress.Trim();
        _port = port;
    }

    // ───────────────────────────── connection ─────────────────────────────

    public async Task<bool> ConnectAsync()
    {
        try
        {
            _tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 17 };
            using (var cts = new CancellationTokenSource(DefaultTimeoutMs))
            {
                await _tcp.ConnectAsync(_ipAddress, _port, cts.Token);
            }
            _stream = _tcp.GetStream();
            _rpos = _rlen = 0;

            var banner = await ReadLineAsync(DefaultTimeoutMs);
            if (!banner.StartsWith("201"))
            {
                Emit($"⚠️ Unexpected XBDM banner: {banner}");
                Disconnect();
                return false;
            }

            _connected = true;
            Emit("✅ XBDM connected");

            // JRPC2 extends "consolefeatures". Stock XBDM answers that command too ("200- DEBUGGING", whatever the
            // parameters), so a 200 alone proves nothing: JRPC2 must be in the module list and answer the kernel-version
            // query (type=13) with a number.
            try
            {
                var mods = await SendRawAsync("modules");
                var names = mods.Lines.Select(l => Regex.Match(l, "name=\"([^\"]+)\"").Groups[1].Value).Where(n => n.Length > 0).ToList();
                bool jrpcModule = names.Any(n => n.StartsWith("JRPC", StringComparison.OrdinalIgnoreCase));
                var r = await SendRawAsync("consolefeatures ver=2 type=13 params=\"A\\0\\A\\0\\\"");
                bool answers = r.Code == 200 && Regex.IsMatch(r.Message, @"^\s*(0x)?[0-9A-Fa-f.]+\s*$");
                IsJRPCConnected = jrpcModule && answers;
                if (IsJRPCConnected) Emit($"✅ JRPC2 plugin loaded (kernel {r.Message.Trim()})");
                else
                {
                    var rpc = names.Where(n => Regex.IsMatch(n, "RPC", RegexOptions.IgnoreCase)).ToList();
                    Emit("ℹ️ JRPC2 is not loaded – remote function calls are unavailable" +
                         (rpc.Count > 0 ? $" (loaded RPC plugins: {string.Join(", ", rpc)})" : "") +
                         ". Copy JRPC2.xex to the path in launch.ini [Plugins] and reboot the console.");
                }
            }
            catch { IsJRPCConnected = false; }

            ConnectionStatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Emit($"❌ XBDM connection failed: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        var wasConnected = _connected;
        _connected = false;
        IsJRPCConnected = false;
        try { _stream?.Close(); } catch { }
        try { _tcp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
        if (wasConnected) ConnectionStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Emit(string msg) => MessageReceived?.Invoke(this, msg);

    private void HandleTransportFailure(Exception ex)
    {
        if (_connected) Emit($"❌ XBDM transport error: {ex.Message}");   // once per session, not once per queued command
        Disconnect();
    }

    // ───────────────────────────── low level IO ─────────────────────────────

    private async Task<int> FillAsync(int timeoutMs)
    {
        // local copy: Disconnect() on another thread may null the field while we are reading
        var stream = _stream ?? throw new IOException("Not connected");
        using var cts = new CancellationTokenSource(timeoutMs);
        int n;
        try
        {
            n = await stream.ReadAsync(_rbuf.AsMemory(0, _rbuf.Length), cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("XBDM read timed out");
        }
        if (n <= 0) throw new IOException("Connection closed by console");
        _rpos = 0;
        _rlen = n;
        return n;
    }

    private async Task<string> ReadLineAsync(int timeoutMs)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_rpos >= _rlen) await FillAsync(timeoutMs);
            while (_rpos < _rlen)
            {
                var b = _rbuf[_rpos++];
                if (b == '\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)b);
            }
        }
    }

    private async Task ReadExactAsync(byte[] dest, int offset, int count, int timeoutMs)
    {
        while (count > 0)
        {
            if (_rpos >= _rlen) await FillAsync(timeoutMs);
            var take = Math.Min(count, _rlen - _rpos);
            Buffer.BlockCopy(_rbuf, _rpos, dest, offset, take);
            _rpos += take;
            offset += take;
            count -= take;
        }
    }

    /// <summary>Pass as binaryLength for responses that carry their own 4-byte length (getfile).</summary>
    public const int LengthPrefixed = -1;

    public sealed class XbdmResponse
    {
        public int Code { get; init; }
        public string Message { get; init; } = "";
        public List<string> Lines { get; } = new();
        public byte[]? Binary { get; set; }
        public bool Ok => Code is >= 200 and < 300;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Code).Append("- ").Append(Message);
            foreach (var l in Lines) sb.Append("\r\n").Append(l);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Sends a command and parses the full response. <paramref name="binaryLength"/> is the number of
    /// raw bytes to read when the console answers 203 (binary follows).
    /// </summary>
    public async Task<XbdmResponse> SendRawAsync(string command, int binaryLength = 0, int timeoutMs = DefaultTimeoutMs)
    {
        if (_stream == null || !_connected) throw new IOException("XBDM not connected");

        await _lock.WaitAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // re-check under the lock: a command that failed while we waited may have closed the session
            var stream = _stream;
            if (stream == null || !_connected) throw new IOException("XBDM not connected");

            // discard anything stale that is sitting in the buffer
            _rpos = _rlen = 0;
            while (stream.DataAvailable)
            {
                var junk = new byte[4096];
                if (await stream.ReadAsync(junk) <= 0) break;
            }

            var data = Encoding.ASCII.GetBytes(command + "\r\n");
            await stream.WriteAsync(data);

            var status = await ReadLineAsync(timeoutMs);
            int code = 0;
            if (status.Length >= 3) int.TryParse(status.AsSpan(0, 3), out code);
            var msg = status.Length > 4 ? status[4..].Trim() : "";
            if (msg.StartsWith("-")) msg = msg[1..].Trim();

            var resp = new XbdmResponse { Code = code, Message = msg };
            if (code == 202)
            {
                while (true)
                {
                    var line = await ReadLineAsync(timeoutMs);
                    if (line == ".") break;
                    resp.Lines.Add(line);
                }
            }
            else if (code == 203 && binaryLength > 0)
            {
                var buf = new byte[binaryLength];
                await ReadExactAsync(buf, 0, binaryLength, timeoutMs);
                resp.Binary = buf;
            }
            else if (code == 203 && binaryLength == LengthPrefixed)
            {
                // getfile style: little-endian uint32 length, then the payload
                var lenBuf = new byte[4];
                await ReadExactAsync(lenBuf, 0, 4, timeoutMs);
                long len = BitConverter.ToUInt32(lenBuf, 0);
                const long keep = 64L << 20;
                var buf = new byte[Math.Min(len, keep)];
                await ReadExactAsync(buf, 0, buf.Length, timeoutMs);
                // never leave unread payload on the socket – it would corrupt the next response
                var sink = new byte[64 << 10];
                for (long rest = len - buf.Length; rest > 0; rest -= sink.Length)
                    await ReadExactAsync(sink, 0, (int)Math.Min(sink.Length, rest), timeoutMs);
                resp.Binary = buf;
            }
            LastLatencyMs = sw.Elapsed.TotalMilliseconds;
            return resp;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or ObjectDisposedException)
        {
            HandleTransportFailure(ex);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Legacy text API: returns the full response as text ("200- OK" style).</summary>
    public async Task<string> SendCommandAsync(string command)
    {
        if (!IsConnected) return "Error: XBDM not connected";
        try
        {
            var r = await SendRawAsync(command);
            return r.ToString();
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    // ───────────────────────────── memory ─────────────────────────────

    // ───── read quarantine ─────
    // A getmem that times out usually means the address is in memory XBDM cannot serve quickly (GPU / uncached /
    // hardware-mapped pages). Asking again from a 50 ms timer locks the debug link up for good, so the 64 KB page is
    // skipped for a while – shared across reconnects (the client object is recreated) and keyed by console.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Host, uint Page), DateTime> Quarantine = new();
    public static TimeSpan QuarantineTime { get; set; } = TimeSpan.FromSeconds(90);

    public bool IsQuarantined(uint address, uint length)
    {
        if (Quarantine.IsEmpty) return false;
        var now = DateTime.UtcNow;
        for (ulong p = address >> 16, last = ((ulong)address + Math.Max(length, 1) - 1) >> 16; p <= last; p++)
            if (Quarantine.TryGetValue((_ipAddress, (uint)p), out var until))
            {
                if (until > now) return true;
                Quarantine.TryRemove((_ipAddress, (uint)p), out _);
            }
        return false;
    }

    /// <summary>Forgets every quarantined page (e.g. after the game changed).</summary>
    public static void ClearQuarantine() => Quarantine.Clear();

    // ───── log throttling ─────
    // Pollers (recorder, watch list, live views) can fail many times a second; the console gets one line per problem
    // every few seconds plus a count of the suppressed repeats.
    private readonly Dictionary<string, (DateTime At, int Suppressed)> _throttle = new();

    private void EmitThrottled(string key, string message)
    {
        string? line = null;
        lock (_throttle)
        {
            var now = DateTime.UtcNow;
            if (_throttle.TryGetValue(key, out var t) && now - t.At < TimeSpan.FromSeconds(5))
                _throttle[key] = (t.At, t.Suppressed + 1);
            else
            {
                line = t.Suppressed > 0 ? $"{message}  (+{t.Suppressed} similar)" : message;
                _throttle[key] = (now, 0);
            }
        }
        if (line != null) Emit(line);
    }

    public async Task<byte[]?> GetMemoryAsync(uint address, uint length)
    {
        if (!IsConnected || length == 0) return null;
        if (IsQuarantined(address, length))
        {
            EmitThrottled("q" + (address >> 16), $"⛔ Skipped read @ 0x{address:X8} – this page timed out recently (unsafe for XBDM)");
            return null;
        }
        uint done = 0;
        try
        {
            var result = new byte[length];
            while (done < length)
            {
                uint chunk = Math.Min(length - done, 0x10000u);
                var part = await ReadChunkAsync(address + done, chunk);
                if (part == null) return done == 0 ? null : result[..(int)done];
                Buffer.BlockCopy(part, 0, result, (int)done, Math.Min(part.Length, (int)chunk));
                done += chunk;
            }
            BytesRead += length;
            if (Verbose) Emit($"📡 Read {length} bytes @ 0x{address:X8}");
            return result;
        }
        catch (TimeoutException ex)
        {
            uint at = address + done;
            Quarantine[(_ipAddress, at >> 16)] = DateTime.UtcNow + QuarantineTime;
            Emit($"❌ Read 0x{at:X8} timed out – page 0x{at & 0xFFFF0000:X8} quarantined for {QuarantineTime.TotalSeconds:0}s so the console can recover ({ex.Message})");
            return null;
        }
        catch (Exception ex)
        {
            EmitThrottled("r" + ex.Message, $"❌ Read 0x{address:X8} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> ReadChunkAsync(uint address, uint length)
    {
        if (_getmem2Supported != false)
        {
            var r = await SendRawAsync($"getmem2 addr=0x{address:X8} length=0x{length:X}", (int)length);
            if (r.Code == 203 && r.Binary != null)
            {
                _getmem2Supported = true;
                return r.Binary;
            }
            if (_getmem2Supported == null && r.Code >= 400 && r.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase))
                _getmem2Supported = false;
            else if (_getmem2Supported == true)
                return null; // supported but failed => unreadable memory
            else
                _getmem2Supported = false;
        }

        // Text fallback: "getmem" returns hex lines, "??" for unreadable bytes.
        var t = await SendRawAsync($"getmem addr=0x{address:X8} length=0x{length:X}");
        if (t.Code != 202) return null;
        var bytes = new byte[length];
        int pos = 0;
        foreach (var line in t.Lines)
        {
            for (int i = 0; i + 1 < line.Length && pos < bytes.Length; i += 2)
            {
                var hi = line[i];
                var lo = line[i + 1];
                if (hi == '?' || lo == '?') { bytes[pos++] = 0; continue; }
                if (IsHex(hi) && IsHex(lo)) bytes[pos++] = (byte)((HexVal(hi) << 4) | HexVal(lo));
            }
        }
        return pos == 0 ? null : bytes;
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static int HexVal(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    public async Task<bool> SetMemoryAsync(uint address, byte[] data)
    {
        if (!IsConnected || data.Length == 0) return false;
        try
        {
            const int chunk = 128;
            for (int off = 0; off < data.Length; off += chunk)
            {
                var n = Math.Min(chunk, data.Length - off);
                var hex = Convert.ToHexString(data, off, n);
                var r = await SendRawAsync($"setmem addr=0x{address + (uint)off:X8} data={hex}");
                if (!r.Ok)
                {
                    Emit($"❌ Write 0x{address + (uint)off:X8} failed: {r.Code} {r.Message}");
                    return false;
                }
            }
            BytesWritten += data.Length;
            if (Verbose) Emit($"📡 Wrote {data.Length} bytes @ 0x{address:X8}");
            return true;
        }
        catch (Exception ex)
        {
            EmitThrottled("w" + ex.Message, $"❌ Write 0x{address:X8} failed: {ex.Message}");
            return false;
        }
    }

    public Task<bool> PokeInt32Async(uint address, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return SetMemoryAsync(address, bytes);
    }

    public Task<bool> PokeFloatAsync(uint address, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return SetMemoryAsync(address, bytes);
    }

    public async Task<uint?> ReadUInt32Async(uint address)
    {
        var d = await GetMemoryAsync(address, 4);
        if (d == null || d.Length < 4) return null;
        return (uint)(d[0] << 24 | d[1] << 16 | d[2] << 8 | d[3]);
    }

    // ───────────────────────────── console / title info ─────────────────────────────

    public async Task<string> GetConsoleName()
    {
        if (!IsConnected) return "Not connected";
        try
        {
            var r = await SendRawAsync("dbgname");
            return r.Ok ? r.Message : "Unknown";
        }
        catch { return "Unknown"; }
    }

    // ───────────────────────────── file system / title control ─────────────────────────────

    public sealed class FileEntry
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public long Size { get; init; }
        public bool IsDirectory { get; init; }
        public DateTime? Modified { get; init; }
        public override string ToString() => Path;
    }

    /// <summary>
    /// Converts kernel device paths reported by "xbeinfo running" (\Device\Harddisk0\Partition1\…) into
    /// XBDM drive paths (Hdd:\…) that getfile / dirlist / magicboot accept.
    /// </summary>
    public static string ToXbdmPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        (string Prefix, string Drive)[] map =
        {
            (@"\Device\Harddisk0\Partition1\", @"Hdd:\"),
            (@"\Device\Harddisk0\SystemPartition\", @"HddX:\"),
            (@"\Device\Mass0\", @"Usb0:\"),
            (@"\Device\Mass1\", @"Usb1:\"),
            (@"\Device\Mass2\", @"Usb2:\"),
            (@"\Device\Cdrom0\", @"Dvd:\"),
            (@"\Device\Flash\", @"Flash:\"),
        };
        foreach (var (prefix, drive) in map)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return drive + path[prefix.Length..];
        return path;
    }

    /// <summary>Reads (part of) a file from the console. Returns null when the file cannot be read.</summary>
    public async Task<byte[]?> GetFileAsync(string path, uint offset = 0, uint size = 0, int timeoutMs = 15000)
    {
        if (!IsConnected) return null;
        var cmd = $"getfile name=\"{ToXbdmPath(path)}\"" + (size > 0 ? $" offset=0x{offset:X} size=0x{size:X}" : "");
        try
        {
            var r = await SendRawAsync(cmd, LengthPrefixed, timeoutMs);
            return r.Code == 203 ? r.Binary : null;
        }
        catch (Exception ex) when (ex is not (IOException or TimeoutException))
        {
            return null;
        }
    }

    public async Task<List<string>> GetDrivesAsync()
    {
        var list = new List<string>();
        if (!IsConnected) return list;
        var r = await SendRawAsync("drivelist");
        foreach (var line in r.Lines.Prepend(r.Message))
        {
            var kv = ParseKeyValues(line);
            if (kv.TryGetValue("drivename", out var d) && d.Length > 0 && !list.Contains(d, StringComparer.OrdinalIgnoreCase)) list.Add(d);
        }
        return list;
    }

    public async Task<List<FileEntry>> ListDirectoryAsync(string path)
    {
        var list = new List<FileEntry>();
        if (!IsConnected) return list;
        var dir = ToXbdmPath(path).TrimEnd('\\') + "\\";
        var r = await SendRawAsync($"dirlist name=\"{dir}\"");
        if (r.Code != 202) return list;
        foreach (var line in r.Lines)
        {
            var kv = ParseKeyValues(line);
            if (!kv.TryGetValue("name", out var name)) continue;
            long size = ((long)ParseUInt(kv.GetValueOrDefault("sizehi")) << 32) | ParseUInt(kv.GetValueOrDefault("sizelo"));
            bool isDir = Regex.IsMatch(line, @"(^|\s)directory(\s|$)", RegexOptions.IgnoreCase);
            DateTime? modified = null;
            try
            {
                long ft = ((long)ParseUInt(kv.GetValueOrDefault("changehi")) << 32) | ParseUInt(kv.GetValueOrDefault("changelo"));
                if (ft > 0) modified = DateTime.FromFileTimeUtc(ft).ToLocalTime();
            }
            catch (ArgumentOutOfRangeException) { }
            list.Add(new FileEntry { Name = name, Path = dir + name, Size = size, IsDirectory = isDir, Modified = modified });
        }
        return list;
    }

    /// <summary>
    /// Streams a whole console file to <paramref name="destination"/> (any size – nothing is buffered in memory).
    /// Returns the number of bytes written; throws when the console refuses the file.
    /// </summary>
    public async Task<long> DownloadFileAsync(string path, Stream destination, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        if (!IsConnected) throw new IOException("XBDM not connected");
        await _lock.WaitAsync(ct);
        try
        {
            var stream = _stream;
            if (stream == null || !_connected) throw new IOException("XBDM not connected");
            _rpos = _rlen = 0;
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"getfile name=\"{ToXbdmPath(path)}\"\r\n"), ct);
            var status = await ReadLineAsync(15000);
            if (!status.StartsWith("203")) throw new IOException($"Console refused the download: {status}");
            var lenBuf = new byte[4];
            await ReadExactAsync(lenBuf, 0, 4, 15000);
            long total = BitConverter.ToUInt32(lenBuf, 0);
            var buf = new byte[64 << 10];
            long done = 0;
            while (done < total)
            {
                // cancellation mid-transfer would desynchronise the session: drain instead of aborting
                int n = (int)Math.Min(buf.Length, total - done);
                await ReadExactAsync(buf, 0, n, 30000);
                if (!ct.IsCancellationRequested) await destination.WriteAsync(buf.AsMemory(0, n), CancellationToken.None);
                done += n;
                BytesRead += n;
                progress?.Report((done, total));
            }
            ct.ThrowIfCancellationRequested();
            return total;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ObjectDisposedException)
        {
            HandleTransportFailure(ex);
            throw;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Uploads <paramref name="length"/> bytes from <paramref name="source"/> to a console path ("sendfile").</summary>
    public async Task UploadFileAsync(string path, Stream source, long length, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        if (!IsConnected) throw new IOException("XBDM not connected");
        if (length > uint.MaxValue) throw new IOException("Files larger than 4 GB cannot be sent over XBDM");
        await _lock.WaitAsync(ct);
        try
        {
            var stream = _stream;
            if (stream == null || !_connected) throw new IOException("XBDM not connected");
            _rpos = _rlen = 0;
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"sendfile name=\"{ToXbdmPath(path)}\" length=0x{length:X}\r\n"), ct);
            var status = await ReadLineAsync(15000);
            if (!status.StartsWith("204")) throw new IOException($"Console refused the upload: {status}");
            var buf = new byte[64 << 10];
            long done = 0;
            while (done < length)
            {
                int n = await source.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, length - done)), CancellationToken.None);
                if (n <= 0) throw new IOException("Local file ended early");
                await stream.WriteAsync(buf.AsMemory(0, n), CancellationToken.None); // never stop half-way: the console expects every byte
                done += n;
                BytesWritten += n;
                progress?.Report((done, length));
            }
            var fin = await ReadLineAsync(30000);
            if (!fin.StartsWith("2")) throw new IOException($"Upload failed: {fin}");
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ObjectDisposedException)
        {
            HandleTransportFailure(ex);
            throw;
        }
        finally { _lock.Release(); }
    }

    public async Task<XbdmResponse> DeleteAsync(string path, bool isDirectory) =>
        await SendRawAsync($"delete name=\"{ToXbdmPath(path)}\"" + (isDirectory ? " dir" : ""));

    public async Task<XbdmResponse> MakeDirectoryAsync(string path) =>
        await SendRawAsync($"mkdir name=\"{ToXbdmPath(path)}\"");

    public async Task<XbdmResponse> RenameAsync(string path, string newPath) =>
        await SendRawAsync($"rename name=\"{ToXbdmPath(path)}\" newname=\"{ToXbdmPath(newPath)}\"");


    /// <summary>Launches an executable on the console (XBDM magicboot). The XBDM session normally survives.</summary>
    public async Task<bool> LaunchTitleAsync(string xexPath)
    {
        if (!IsConnected) return false;
        var path = ToXbdmPath(xexPath);
        var dir = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : path;
        var r = await SendRawAsync($"magicboot title=\"{path}\" directory=\"{dir}\"", 0, 10000);
        return r.Ok;
    }

    /// <summary>Warm (dashboard) or cold restart. The connection drops; reconnect afterwards.</summary>
    public async Task<bool> RebootAsync(bool cold = false)
    {
        if (!IsConnected) return false;
        try { var r = await SendRawAsync(cold ? "magicboot cold" : "magicboot", 0, 5000); return r.Ok; }
        catch (IOException) { return true; } // console closed the socket while rebooting
    }

    // ───────────────────────────── memory map / threads / execution ─────────────────────────────

    public sealed class MemoryRegion
    {
        public uint Base { get; init; }
        public uint Size { get; init; }
        public uint Protect { get; init; }
        public uint End => Base + Size;
        public string ProtectText
        {
            get
            {
                // Xbox 360 page protection (Win32 PAGE_* values)
                var p = Protect & 0xFF;
                string s = p switch
                {
                    0x01 => "---", 0x02 => "R--", 0x04 => "RW-", 0x08 => "RW- (copy)",
                    0x10 => "--X", 0x20 => "R-X", 0x40 => "RWX", 0x80 => "RWX (copy)", _ => $"0x{p:X2}",
                };
                if ((Protect & 0x100) != 0) s += " guard";
                if ((Protect & 0x200) != 0) s += " nocache";
                if ((Protect & 0x400) != 0) s += " wc";
                return s;
            }
        }
    }

    /// <summary>Every committed memory region of the console ("walkmem").</summary>
    public async Task<List<MemoryRegion>> WalkMemoryAsync()
    {
        var list = new List<MemoryRegion>();
        if (!IsConnected) return list;
        var r = await SendRawAsync("walkmem", 0, 15000);
        foreach (var line in r.Lines)
        {
            var kv = ParseKeyValues(line);
            if (!kv.ContainsKey("base")) continue;
            list.Add(new MemoryRegion
            {
                Base = ParseUInt(kv.GetValueOrDefault("base")),
                Size = ParseUInt(kv.GetValueOrDefault("size")),
                Protect = ParseUInt(kv.GetValueOrDefault("protect")),
            });
        }
        return list;
    }

    /// <summary>Thread ids of the running process ("threads").</summary>
    public async Task<List<uint>> GetThreadsAsync()
    {
        var list = new List<uint>();
        if (!IsConnected) return list;
        var r = await SendRawAsync("threads");
        foreach (var line in r.Lines)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            uint id = ParseUInt(t);
            if (id != 0) list.Add(id);
        }
        return list;
    }

    /// <summary>Raw key/value info for a thread ("threadinfo"): start, priority, suspend, tlsbase, base, limit, nameaddr, …</summary>
    public async Task<Dictionary<string, string>> GetThreadInfoAsync(uint threadId)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!IsConnected) return d;
        var r = await SendRawAsync($"threadinfo thread=0x{threadId:X}");
        foreach (var line in r.Lines.Prepend(r.Message))
            foreach (var (k, v) in ParseKeyValues(line)) d[k] = v;
        return d;
    }

    public async Task<bool> SuspendThreadAsync(uint threadId) => IsConnected && (await SendRawAsync($"suspend thread=0x{threadId:X}")).Ok;
    public async Task<bool> ResumeThreadAsync(uint threadId) => IsConnected && (await SendRawAsync($"resume thread=0x{threadId:X}")).Ok;

    /// <summary>Halts every title thread (XBDM "stop").</summary>
    public async Task<bool> StopExecutionAsync() => IsConnected && (await SendRawAsync("stop")).Ok;

    /// <summary>Resumes execution after <see cref="StopExecutionAsync"/> (XBDM "go").</summary>
    public async Task<bool> GoAsync() => IsConnected && (await SendRawAsync("go")).Ok;

    /// <summary>Reads a block that may be larger than one request, in chunks; unreadable chunks stay zero.</summary>
    public async Task<(byte[] Data, int FailedChunks)> ReadLargeAsync(uint address, uint length, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var data = new byte[length];
        const uint chunk = 0x10000;
        int failed = 0;
        for (uint off = 0; off < length; off += chunk)
        {
            ct.ThrowIfCancellationRequested();
            uint n = Math.Min(chunk, length - off);
            var part = await GetMemoryAsync(address + off, n);
            if (part != null) Buffer.BlockCopy(part, 0, data, (int)off, Math.Min(part.Length, (int)n));
            else failed++;
            progress?.Report((double)(off + n) / length);
        }
        return (data, failed);
    }

    /// <summary>Parses XBDM key=value lines, e.g. name="default.xex" base=0x82000000 size=0x...</summary>
    public static Dictionary<string, string> ParseKeyValues(string line)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(line, "(\\w+)=(\"[^\"]*\"|\\S+)"))
            d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return d;
    }

    public static uint ParseUInt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], NumberStyles.HexNumber, null, out var h) ? h : 0;
        if (uint.TryParse(s, out var d)) return d;
        if (int.TryParse(s, out var i)) return unchecked((uint)i);
        return 0;
    }

    public sealed class ModuleInfo
    {
        public string Name { get; set; } = "";
        public uint Base { get; set; }
        public uint Size { get; set; }
        public uint Checksum { get; set; }
        public uint Timestamp { get; set; }
        public override string ToString() => $"{Name} @0x{Base:X8} (0x{Size:X})";
    }

    public sealed class SectionInfo
    {
        public string Name { get; set; } = "";
        public uint Base { get; set; }
        public uint Size { get; set; }
        public uint Flags { get; set; }
    }

    public async Task<List<ModuleInfo>> GetModulesAsync()
    {
        var list = new List<ModuleInfo>();
        if (!IsConnected) return list;
        var r = await SendRawAsync("modules");
        foreach (var line in r.Lines)
        {
            var kv = ParseKeyValues(line);
            if (!kv.TryGetValue("name", out var name)) continue;
            list.Add(new ModuleInfo
            {
                Name = name,
                Base = ParseUInt(kv.GetValueOrDefault("base")),
                Size = ParseUInt(kv.GetValueOrDefault("size")),
                Checksum = ParseUInt(kv.GetValueOrDefault("check")),
                Timestamp = ParseUInt(kv.GetValueOrDefault("timestamp")),
            });
        }
        return list;
    }

    public async Task<List<SectionInfo>> GetModuleSectionsAsync(string moduleName)
    {
        var list = new List<SectionInfo>();
        if (!IsConnected) return list;
        var r = await SendRawAsync($"modsections name=\"{moduleName}\"");
        foreach (var line in r.Lines)
        {
            var kv = ParseKeyValues(line);
            if (!kv.TryGetValue("name", out var name)) continue;
            list.Add(new SectionInfo
            {
                Name = name,
                Base = ParseUInt(kv.GetValueOrDefault("base")),
                Size = ParseUInt(kv.GetValueOrDefault("size")),
                Flags = ParseUInt(kv.GetValueOrDefault("flags")),
            });
        }
        return list;
    }

    /// <summary>Returns (path, timestamp, checksum) of the running title via "xbeinfo running".</summary>
    public async Task<(string Path, uint Timestamp, uint Checksum)?> GetRunningTitleAsync()
    {
        if (!IsConnected) return null;
        var r = await SendRawAsync("xbeinfo running");
        if (!r.Ok) return null;
        string path = "";
        uint ts = 0, ck = 0;
        foreach (var line in r.Lines.Prepend(r.Message))
        {
            var kv = ParseKeyValues(line);
            if (kv.TryGetValue("name", out var n)) path = n;
            if (kv.TryGetValue("timestamp", out var t)) ts = ParseUInt(t);
            if (kv.TryGetValue("checksum", out var c)) ck = ParseUInt(c);
        }
        return (path, ts, ck);
    }

    /// <summary>
    /// Calls a function on the console through the JRPC2 plugin and returns r3. Only integer / pointer arguments are
    /// sent (strings are written to console memory by the caller and passed as pointers), which keeps the request in
    /// the plugin's simplest, most widely supported form:
    ///   consolefeatures ver=2 type=1 system as=0 params="A\{address}\A\{argc}\1\{int}\1\{int}…\"
    /// Long-running calls answer "buf_addr=…"; the result is then polled with "consolefeatures buf_addr=…".
    /// </summary>
    public async Task<uint?> JrpcCallAsync(uint address, bool systemThread, params uint[] args)
    {
        if (!IsConnected) throw new IOException("XBDM not connected");
        if (!IsJRPCConnected) throw new InvalidOperationException("The JRPC2 plugin is not loaded on the console – remote calls need it");
        if (args.Length > 37) throw new ArgumentException("JRPC supports at most 37 arguments");
        var sb = new StringBuilder();
        sb.Append("consolefeatures ver=2 type=1").Append(systemThread ? " system" : "")
          .Append(" as=0 params=\"A\\").Append(address.ToString("X")).Append("\\A\\").Append(args.Length).Append('\\');
        foreach (var a in args) sb.Append("1\\").Append(unchecked((int)a)).Append('\\');
        sb.Append('"');

        var r = await SendRawAsync(sb.ToString(), timeoutMs: 10000);
        for (int i = 0; i < 40 && r.Message.Contains("buf_addr=", StringComparison.OrdinalIgnoreCase); i++)
        {
            await Task.Delay(100);
            var buf = Regex.Match(r.Message, @"buf_addr=(?:0x)?([0-9A-Fa-f]+)").Groups[1].Value;
            r = await SendRawAsync($"consolefeatures buf_addr=0x{buf}", timeoutMs: 10000);
        }
        if (r.Code != 200) throw new IOException($"JRPC call 0x{address:X8} failed: {r.Code} {r.Message}");
        var m = Regex.Match(r.Message, @"([0-9A-Fa-f]{1,8})\s*$");
        return m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out var v) ? v : null;
    }

    /// <summary>Title ID through the JRPC2 plugin (XamGetCurrentTitleId). Null when unavailable.</summary>
    public async Task<uint?> GetTitleIdAsync()
    {
        if (!IsConnected || !IsJRPCConnected) return null;
        try
        {
            var r = await SendRawAsync("consolefeatures ver=2 type=16 params=\"A\\0\\A\\0\\\"");
            if (r.Code != 200) return null;
            var m = Regex.Match(r.Message, "([0-9A-Fa-f]{8})");
            if (m.Success && uint.TryParse(m.Value, NumberStyles.HexNumber, null, out var id) && id != 0)
                return id;
        }
        catch { }
        return null;
    }

    // ───────────────────────────── registers (legacy behaviour retained) ─────────────────────────────

    public async Task<Dictionary<string, ulong>> GetRegistersAsync()
    {
        var registers = new Dictionary<string, ulong>();
        if (!IsConnected) return registers;

        try
        {
            Emit("Reading registers via XBDM...");
            var haltResponse = await SendCommandAsync("stop");   // XBDM has no "halt" command
            Emit($"Halt response: {haltResponse}");
            await Task.Delay(300);

            var threadIds = await TryListThreadsAsync();
            foreach (var tid in threadIds)
            {
                var ctxResp = await SendCommandAsync($"getcontext thread={tid} control int fp");
                var parsed = ParseRegisterResponse(ctxResp);
                if (parsed.Count == 0)
                {
                    ctxResp = await SendCommandAsync($"getcontext {tid}");
                    parsed = ParseRegisterResponse(ctxResp);
                }
                if (parsed.Count > 0)
                {
                    foreach (var kv in parsed) registers[kv.Key] = kv.Value;
                    Emit($"Parsed {parsed.Count} registers from thread {tid}");
                    break;
                }
            }

            if (registers.Count == 0)
            {
                foreach (var reg in Enumerable.Range(0, 32).Select(i => $"r{i}").Concat(new[] { "pc", "lr", "ctr", "xer", "cr", "msr" }))
                {
                    var response = await SendCommandAsync($"getreg {reg}");
                    var value = ParseRegisterValue(response);
                    if (value.HasValue)
                        registers[reg.StartsWith("r") ? reg : reg.ToUpperInvariant()] = value.Value;
                }
            }

            var resumeResponse = await SendCommandAsync("go");
            Emit($"Resume response: {resumeResponse}");
            Emit($"📊 Read {registers.Count} registers");
        }
        catch (Exception ex)
        {
            Emit($"❌ Register read error: {ex.Message}");
        }
        return registers;
    }

    public async Task<bool> SetRegisterAsync(string registerName, ulong value)
    {
        if (!IsConnected) return false;
        var response = await SendCommandAsync($"setreg {registerName} 0x{value:X16}");
        bool success = response.StartsWith("200") || response.StartsWith("202");
        Emit(success ? $"✅ Set {registerName} = 0x{value:X16}" : $"❌ Failed to set {registerName}: {response}");
        return success;
    }

    private static ulong? ParseRegisterValue(string response)
    {
        foreach (var line in response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = line.Trim();
            if (!(clean.StartsWith("200-") || clean.StartsWith("202-"))) continue;
            var v = clean[4..].Trim();
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
            if (ulong.TryParse(v, NumberStyles.HexNumber, null, out var r)) return r;
        }
        return null;
    }

    private Dictionary<string, ulong> ParseRegisterResponse(string response)
    {
        var registers = new Dictionary<string, ulong>();
        foreach (var line in response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("4")) continue;
            foreach (Match m in Regex.Matches(line, @"(\w+)=0x([0-9A-Fa-f]+)"))
            {
                var name = m.Groups[1].Value;
                if (!ulong.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, null, out var value)) continue;
                if ((name.StartsWith("r") || name.StartsWith("Gpr", StringComparison.OrdinalIgnoreCase)) &&
                    int.TryParse(Regex.Match(name, @"\d+").Value, out var n) && Regex.IsMatch(name, @"^(r|Gpr)\d+$", RegexOptions.IgnoreCase))
                    registers[$"r{n}"] = value;
                else
                    registers[name.ToUpperInvariant() switch { "IAR" => "PC", var x => x }] = value;
            }
        }
        return registers;
    }

    private async Task<List<string>> TryListThreadsAsync()
    {
        var ids = new List<string>();
        var r = await SendRawAsync("threads");
        foreach (var line in r.Lines)
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("2")) ids.Add(t);
        }
        return ids;
    }

    public string GetConnectionStatus()
    {
        if (!IsConnected) return "Disconnected";
        return IsJRPCConnected ? "XBDM + JRPC2" : "XBDM";
    }

    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
