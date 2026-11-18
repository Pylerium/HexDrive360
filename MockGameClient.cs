using System.Threading.Tasks;

namespace MemoryEditor;

// Simple mock client for self-test purposes
public class MockGameClient : IGameClient
{
    private Dictionary<uint, byte[]> _mem = new();
    public bool IsConnected => true;

    public MockGameClient()
    {
        // Seed some fake memory for testing
        var bytes = new byte[1024];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i & 0xFF);
        _mem[0x1000] = bytes;
        // place a pointer chain example
        var ptr = BitConverter.GetBytes((uint)0x2000);
        _mem[0x3000] = ptr;
        _mem[0x2000] = BitConverter.GetBytes((uint)0x4000);
        _mem[0x4000] = BitConverter.GetBytes((uint)0x5000);

        // Also seed addresses in the safe range used by ScriptExecutor
        var safeBytes = new byte[512];
        for (int i = 0; i < safeBytes.Length; i++) safeBytes[i] = (byte)((i + 1) & 0xFF);
        _mem[0x80001000] = safeBytes;
        _mem[0x80002000] = BitConverter.GetBytes((uint)0x80004000);
        _mem[0x80003000] = BitConverter.GetBytes((uint)0x80002000);
        _mem[0x80004000] = BitConverter.GetBytes((uint)0x80005000);
        _mem[0x80005000] = BitConverter.GetBytes((uint)0x80006000);
    }

    public Task<byte[]?> GetMemoryAsync(uint address, uint length)
    {
        // return a slice if available
        if (_mem.TryGetValue(address, out var block))
        {
            var len = (int)Math.Min(length, (uint)block.Length);
            var outb = new byte[len];
            Array.Copy(block, 0, outb, 0, len);
            return Task.FromResult<byte[]?>(outb);
        }
        // If address inside a block
        foreach (var kv in _mem)
        {
            var baseAddr = kv.Key;
            var blockBytes = kv.Value;
            if (address >= baseAddr && address + length <= baseAddr + (uint)blockBytes.Length)
            {
                var offset = (int)(address - baseAddr);
                var outb = new byte[length];
                Array.Copy(blockBytes, offset, outb, 0, (int)length);
                return Task.FromResult<byte[]?>(outb);
            }
        }
        // return dummy data
        var dummy = new byte[length];
        return Task.FromResult<byte[]?>(dummy);
    }

    public Task<bool> SetMemoryAsync(uint address, byte[] data)
    {
        // store/overwrite a block starting at address
        var copy = new byte[data.Length];
        Array.Copy(data, copy, data.Length);
        _mem[address] = copy;
        return Task.FromResult(true);
    }

    public Task<bool> PokeInt32Async(uint address, int value)
    {
        var b = BitConverter.GetBytes(value);
        return SetMemoryAsync(address, b);
    }
}
