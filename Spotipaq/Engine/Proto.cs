// Proto.cs - just enough protobuf (proto2 wire format) to talk to Spotify, without any library.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Spotipaq.Engine
{
    public sealed class ProtoWriter
    {
        readonly MemoryStream _ms = new MemoryStream();

        void Key(int field, int wire) { Varint((ulong)((field << 3) | wire)); }

        void Varint(ulong v)
        {
            while (v >= 0x80) { _ms.WriteByte((byte)(v | 0x80)); v >>= 7; }
            _ms.WriteByte((byte)v);
        }

        public ProtoWriter Int(int field, long value) { Key(field, 0); Varint((ulong)value); return this; }
        public ProtoWriter Bool(int field, bool value) { return Int(field, value ? 1 : 0); }
        public ProtoWriter Bytes(int field, byte[] value)
        {
            Key(field, 2);
            Varint((ulong)value.Length);
            _ms.Write(value, 0, value.Length);
            return this;
        }
        public ProtoWriter String(int field, string value) { return Bytes(field, Encoding.UTF8.GetBytes(value ?? "")); }
        public ProtoWriter Message(int field, ProtoWriter inner) { return Bytes(field, inner.ToArray()); }
        public byte[] ToArray() { return _ms.ToArray(); }
    }

    /// <summary>One decoded field. Varints are in Int; length-delimited values in Data.</summary>
    public struct ProtoField
    {
        public int Number, Wire;
        public long Int;
        public byte[] Data;
        public string Text { get { return Data == null ? "" : Encoding.UTF8.GetString(Data); } }
        public ProtoReader Message { get { return new ProtoReader(Data ?? new byte[0]); } }
    }

    public sealed class ProtoReader
    {
        readonly byte[] _b;
        int _pos;
        readonly int _end;

        public ProtoReader(byte[] data) : this(data, 0, data.Length) { }
        public ProtoReader(byte[] data, int offset, int length) { _b = data; _pos = offset; _end = offset + length; }

        ulong Varint()
        {
            ulong v = 0;
            int shift = 0;
            while (true)
            {
                if (_pos >= _end) throw new InvalidDataException("Truncated varint");
                byte b = _b[_pos++];
                v |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("Varint too long");
            }
        }

        public bool Next(out ProtoField f)
        {
            f = new ProtoField();
            if (_pos >= _end) return false;
            ulong key = Varint();
            f.Number = (int)(key >> 3);
            f.Wire = (int)(key & 7);
            switch (f.Wire)
            {
                case 0: f.Int = (long)Varint(); break;
                case 1: f.Int = BitConverter.ToInt64(_b, _pos); _pos += 8; break;
                case 2:
                    int len = (int)Varint();
                    if (len < 0 || _pos + len > _end) throw new InvalidDataException("Bad length");
                    f.Data = new byte[len];
                    Buffer.BlockCopy(_b, _pos, f.Data, 0, len);
                    _pos += len;
                    break;
                case 5: f.Int = BitConverter.ToInt32(_b, _pos); _pos += 4; break;
                default: throw new InvalidDataException("Unsupported wire type " + f.Wire);
            }
            return true;
        }

        /// <summary>All fields, grouped by number (repeated fields keep their order).</summary>
        public Dictionary<int, List<ProtoField>> ReadAll()
        {
            Dictionary<int, List<ProtoField>> d = new Dictionary<int, List<ProtoField>>();
            ProtoField f;
            while (Next(out f))
            {
                List<ProtoField> list;
                if (!d.TryGetValue(f.Number, out list)) { list = new List<ProtoField>(); d[f.Number] = list; }
                list.Add(f);
            }
            return d;
        }

        /// <summary>sint32 / sint64 values are zigzag encoded.</summary>
        public static int Zigzag(long v)
        {
            unchecked { ulong x = (ulong)v; return (int)(long)((x >> 1) ^ (ulong)(-(long)(x & 1))); }
        }

        /// <summary>Convenience: the first field with this number, or null.</summary>
        public static ProtoField? First(Dictionary<int, List<ProtoField>> d, int number)
        {
            List<ProtoField> list;
            return d.TryGetValue(number, out list) && list.Count > 0 ? (ProtoField?)list[0] : null;
        }
    }
}
