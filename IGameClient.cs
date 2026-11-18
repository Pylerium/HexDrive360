using System.Threading.Tasks;

namespace MemoryEditor;

public interface IGameClient
{
    bool IsConnected { get; }
    Task<byte[]?> GetMemoryAsync(uint address, uint length);
    Task<bool> SetMemoryAsync(uint address, byte[] data);
    Task<bool> PokeInt32Async(uint address, int value);
}