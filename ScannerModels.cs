using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MemoryEditor
{
    public class ScanOptions
    {
        public uint Start { get; set; }
        public uint End { get; set; }
        public string ValueType { get; set; } = "int32";
        public string ValueText { get; set; } = "";
        public int ChunkSize { get; set; } = 0x1000;
        public int Concurrency { get; set; } = 6;
        public bool BigEndian { get; set; } = true; // PPC64BE
    }

    public class ScanResult
    {
        public uint Address { get; set; }
        public string ValueHex { get; set; } = "";
        public string ValueText { get; set; } = "";
        [JsonIgnore]
        public byte[] Raw { get; set; } = Array.Empty<byte>();
    }

    public class ScanSession
    {
        public ScanOptions Options { get; set; } = new ScanOptions();
        public List<ScanResult> Results { get; set; } = new List<ScanResult>();
    }
}
