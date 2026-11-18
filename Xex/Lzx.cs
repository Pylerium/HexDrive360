namespace MemoryEditor.Xex;

/// <summary>
/// Microsoft LZX decompressor (the variant used by CAB files and Xbox 360 XEX "normal" compression).
/// Clean-room port of the algorithm as documented by libmspack's lzxd.c: 32 KiB frames,
/// verbatim / aligned / uncompressed blocks, repeated-offset LRU, delta-coded Huffman tree lengths.
/// The whole output is used as the sliding window, which is exact because the output size is known.
/// </summary>
public sealed class LzxDecoder
{
    private const int MinMatch = 2;
    private const int NumChars = 256;
    private const int PretreeSymbols = 20;
    private const int AlignedSymbols = 8;
    private const int SecondaryLengths = 249;
    private const int FrameSize = 32768;

    private static readonly int[] PositionSlots = { 30, 32, 34, 36, 38, 42, 50, 66, 98, 162, 290 };
    private static readonly byte[] ExtraBits = new byte[290];
    private static readonly uint[] PositionBase = new uint[290];

    static LzxDecoder()
    {
        for (int i = 0, j = 0; i < 290; i += 2)
        {
            ExtraBits[i] = (byte)j;
            ExtraBits[i + 1] = (byte)j;
            if (i != 0 && j < 17) j++;
        }
        uint b = 0;
        for (int i = 0; i < 290; i++)
        {
            PositionBase[i] = b;
            b += 1u << ExtraBits[i];
        }
    }

    // bit reader state (16-bit little-endian words, MSB first)
    private readonly byte[] _in;
    private int _ip;
    private ulong _bitBuf;
    private int _bitsLeft;

    private readonly int _mainElements;
    private readonly byte[] _mainLen;
    private readonly byte[] _lenLen = new byte[SecondaryLengths + 32];
    private readonly byte[] _alignedLen = new byte[AlignedSymbols];
    private readonly byte[] _preLen = new byte[PretreeSymbols];

    private Huffman _main = null!, _length = null!, _aligned = null!;

    private LzxDecoder(byte[] input, int windowBits)
    {
        if (windowBits < 15 || windowBits > 21) throw new InvalidDataException($"Unsupported LZX window size 2^{windowBits}");
        _in = input;
        _mainElements = NumChars + (PositionSlots[windowBits - 15] << 3);
        _mainLen = new byte[_mainElements + 32];
    }

    /// <summary>Bytes decoded before an error (for partial recovery).</summary>
    private int _decoded;

    public static byte[] Decompress(byte[] input, int outputLength, int windowSize, bool allowPartial = false)
    {
        int bits = 0;
        while ((1 << bits) < windowSize) bits++;
        if ((1 << bits) != windowSize) throw new InvalidDataException($"LZX window size 0x{windowSize:X} is not a power of two");
        var d = new LzxDecoder(input, bits);
        var window = new byte[outputLength];
        var output = new byte[outputLength];
        try
        {
            d.Run(window, output);
        }
        catch (InvalidDataException) when (allowPartial && d._decoded > outputLength / 2)
        {
            // trailing padding of some images is not part of the LZX stream – keep what was decoded
            Buffer.BlockCopy(window, 0, output, 0, d._decoded);
        }
        return output;
    }

    // ── bit IO ──

    private void Ensure(int n)
    {
        while (_bitsLeft < n)
        {
            int b0 = _ip < _in.Length ? _in[_ip] : 0;
            int b1 = _ip + 1 < _in.Length ? _in[_ip + 1] : 0;
            _ip += 2;
            _bitBuf |= (ulong)((b1 << 8) | b0) << (64 - 16 - _bitsLeft);
            _bitsLeft += 16;
        }
    }

    private uint PeekBits(int n) => (uint)(_bitBuf >> (64 - n));

    private void Remove(int n)
    {
        _bitBuf <<= n;
        _bitsLeft -= n;
    }

    private uint ReadBits(int n)
    {
        if (n == 0) return 0;
        Ensure(n);
        var v = PeekBits(n);
        Remove(n);
        return v;
    }

    private int ReadSym(Huffman h)
    {
        if (h.Empty) throw new InvalidDataException("LZX: read from empty Huffman tree");
        Ensure(Huffman.MaxBits);
        var entry = h.Table[PeekBits(Huffman.MaxBits)];
        int len = entry >> 16;
        if (len == 0) throw new InvalidDataException("LZX: invalid Huffman code");
        Remove(len);
        return entry & 0xFFFF;
    }

    // ── tree reading ──

    private void ReadLengths(byte[] lens, int first, int last)
    {
        for (int x = 0; x < PretreeSymbols; x++) _preLen[x] = (byte)ReadBits(4);
        var pre = new Huffman(_preLen, PretreeSymbols);

        for (int x = first; x < last;)
        {
            int z = ReadSym(pre);
            if (z == 17)
            {
                int y = (int)ReadBits(4) + 4;
                while (y-- > 0 && x < lens.Length) lens[x++] = 0;
            }
            else if (z == 18)
            {
                int y = (int)ReadBits(5) + 20;
                while (y-- > 0 && x < lens.Length) lens[x++] = 0;
            }
            else if (z == 19)
            {
                int y = (int)ReadBits(1) + 4;
                z = ReadSym(pre);
                z = lens[x] - z;
                if (z < 0) z += 17;
                while (y-- > 0 && x < lens.Length) lens[x++] = (byte)z;
            }
            else
            {
                z = lens[x] - z;
                if (z < 0) z += 17;
                lens[x++] = (byte)z;
            }
        }
    }

    // ── main loop ──

    private void Run(byte[] window, byte[] output)
    {
        uint r0 = 1, r1 = 1, r2 = 1;
        int blockType = 0, blockRemaining = 0, blockLength = 0;
        int pos = 0;
        int outLen = window.Length;
        bool headerRead = false;
        uint intelFileSize = 0;
        bool intelStarted = false;

        while (pos < outLen)
        {
            if (!headerRead)
            {
                if (ReadBits(1) != 0)
                {
                    uint hi = ReadBits(16), lo = ReadBits(16);
                    intelFileSize = (hi << 16) | lo;
                }
                headerRead = true;
            }

            int frameStart = pos;
            int frameSize = Math.Min(FrameSize, outLen - pos);
            int frameEnd = pos + frameSize;

            while (pos < frameEnd)
            {
                if (blockRemaining == 0)
                {
                    if (blockType == 3)
                    {
                        // uncompressed blocks are padded to an even length
                        if ((blockLength & 1) != 0) _ip++;
                        _bitBuf = 0;
                        _bitsLeft = 0;
                    }

                    blockType = (int)ReadBits(3);
                    uint hi = ReadBits(16), lo = ReadBits(8);
                    blockRemaining = blockLength = (int)((hi << 8) | lo);

                    switch (blockType)
                    {
                        case 2: // aligned
                            for (int i = 0; i < AlignedSymbols; i++) _alignedLen[i] = (byte)ReadBits(3);
                            _aligned = new Huffman(_alignedLen, AlignedSymbols);
                            goto case 1;
                        case 1: // verbatim
                            ReadLengths(_mainLen, 0, 256);
                            ReadLengths(_mainLen, 256, _mainElements);
                            _main = new Huffman(_mainLen, _mainElements);
                            if (_mainLen[0xE8] != 0) intelStarted = true;
                            ReadLengths(_lenLen, 0, SecondaryLengths);
                            _length = new Huffman(_lenLen, SecondaryLengths, allowEmpty: true);
                            break;
                        case 3: // uncompressed
                        {
                            intelStarted = true;
                            if (_bitsLeft == 0) Ensure(16);
                            _bitsLeft = 0;
                            _bitBuf = 0;
                            // the bit reader consumed whole 16-bit words; raw bytes continue at _ip
                            if (_ip + 12 > _in.Length) throw new InvalidDataException("LZX: truncated uncompressed block header");
                            r0 = BitConverter.ToUInt32(_in, _ip);
                            r1 = BitConverter.ToUInt32(_in, _ip + 4);
                            r2 = BitConverter.ToUInt32(_in, _ip + 8);
                            _ip += 12;
                            break;
                        }
                        default:
                            throw new InvalidDataException($"LZX: bad block type {blockType}");
                    }
                }

                int run = Math.Min(blockRemaining, frameEnd - pos);
                blockRemaining -= run;
                int target = pos + run;

                if (blockType == 3)
                {
                    if (_ip + run > _in.Length) throw new InvalidDataException("LZX: truncated uncompressed block");
                    Buffer.BlockCopy(_in, _ip, window, pos, run);
                    _ip += run;
                    pos += run;
                    continue;
                }

                bool aligned = blockType == 2;
                while (pos < target)
                {
                    int main = ReadSym(_main);
                    if (main < NumChars)
                    {
                        window[pos++] = (byte)main;
                        continue;
                    }

                    main -= NumChars;
                    int matchLength = main & 7;
                    if (matchLength == 7) matchLength += ReadSym(_length);
                    matchLength += MinMatch;

                    uint matchOffset = (uint)(main >> 3);
                    switch (matchOffset)
                    {
                        case 0: matchOffset = r0; break;
                        case 1: matchOffset = r1; r1 = r0; r0 = matchOffset; break;
                        case 2: matchOffset = r2; r2 = r0; r0 = matchOffset; break;
                        default:
                        {
                            int slot = (int)matchOffset;
                            int extra = slot >= 36 ? 17 : ExtraBits[slot];
                            matchOffset = PositionBase[slot] - 2;
                            if (!aligned)
                            {
                                matchOffset += ReadBits(extra);
                            }
                            else if (extra > 3)
                            {
                                matchOffset += ReadBits(extra - 3) << 3;
                                matchOffset += (uint)ReadSym(_aligned);
                            }
                            else if (extra == 3)
                            {
                                matchOffset += (uint)ReadSym(_aligned);
                            }
                            else if (extra > 0)
                            {
                                matchOffset += ReadBits(extra);
                            }
                            else
                            {
                                matchOffset = 1;
                            }
                            r2 = r1; r1 = r0; r0 = matchOffset;
                            break;
                        }
                    }

                    if (matchOffset == 0 || matchOffset > pos)
                        throw new InvalidDataException($"LZX: match offset {matchOffset} beyond start of data at {pos}");
                    if (pos + matchLength > outLen)
                        throw new InvalidDataException("LZX: match runs past end of output");

                    _decoded = pos;
                    int src = pos - (int)matchOffset;
                    for (int k = 0; k < matchLength; k++) window[pos + k] = window[src + k];
                    pos += matchLength;
                }

                // a match may overrun the requested run within the same block
                if (pos > target)
                {
                    int over = pos - target;
                    if (over > blockRemaining) throw new InvalidDataException("LZX: match overran block");
                    blockRemaining -= over;
                }
            }

            _decoded = pos;
            if (pos != frameEnd) throw new InvalidDataException("LZX: decoded data crossed a frame boundary");

            // re-align bitstream to 16 bits after each frame
            if (_bitsLeft > 0) Ensure(16);
            if ((_bitsLeft & 15) != 0) Remove(_bitsLeft & 15);

            // copy the frame out; E8 translation is undone on the copy so the window keeps raw bytes
            Buffer.BlockCopy(window, frameStart, output, frameStart, frameSize);
            if (intelStarted && intelFileSize != 0 && frameStart < (1 << 30) && frameSize > 10)
            {
                int curPos = frameStart;
                int end = frameStart + frameSize - 10;
                for (int i = frameStart; i < end; i++)
                {
                    if (output[i] != 0xE8) continue;
                    curPos = i;
                    int absOff = output[i + 1] | output[i + 2] << 8 | output[i + 3] << 16 | output[i + 4] << 24;
                    if (absOff >= -curPos && absOff < intelFileSize)
                    {
                        int relOff = absOff >= 0 ? absOff - curPos : absOff + (int)intelFileSize;
                        output[i + 1] = (byte)relOff;
                        output[i + 2] = (byte)(relOff >> 8);
                        output[i + 3] = (byte)(relOff >> 16);
                        output[i + 4] = (byte)(relOff >> 24);
                    }
                    i += 4;
                }
            }
        }
    }

    /// <summary>Canonical Huffman decode table (direct lookup, 16-bit max code length).</summary>
    private sealed class Huffman
    {
        public const int MaxBits = 16;
        public readonly int[] Table = new int[1 << MaxBits]; // (len << 16) | symbol
        public readonly bool Empty;

        public Huffman(byte[] lengths, int count, bool allowEmpty = false)
        {
            var blCount = new int[MaxBits + 1];
            for (int i = 0; i < count; i++) blCount[lengths[i]]++;
            blCount[0] = 0;
            if (blCount.Sum() == 0)
            {
                if (!allowEmpty) throw new InvalidDataException("LZX: empty Huffman tree");
                Empty = true;
                return;
            }

            var next = new int[MaxBits + 2];
            int code = 0;
            for (int bits = 1; bits <= MaxBits; bits++)
            {
                code = (code + blCount[bits - 1]) << 1;
                next[bits] = code;
            }

            for (int sym = 0; sym < count; sym++)
            {
                int len = lengths[sym];
                if (len == 0) continue;
                if (len > MaxBits) throw new InvalidDataException("LZX: code too long");
                if (sym > 0xFFF) throw new InvalidDataException("LZX: symbol out of range");
                int c = next[len]++;
                if (c >= (1 << len)) throw new InvalidDataException("LZX: over-subscribed Huffman tree");
                int fill = MaxBits - len;
                int start = c << fill;
                int entry = (len << 16) | sym;
                for (int k = 0; k < (1 << fill); k++) Table[start + k] = entry;
            }
        }
    }
}
