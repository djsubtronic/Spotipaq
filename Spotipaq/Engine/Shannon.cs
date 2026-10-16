// Shannon.cs - the Shannon stream cipher + MAC used on the Spotify access-point connection.
// Straight port of librespot-java's Shannon.java (itself from the reference C implementation).
using System;

namespace Spotipaq.Engine
{
    public sealed class Shannon
    {
        const int N = 16, FOLD = N, INITKONST = 0x6996c53a, KEYP = 13;
        readonly int[] R = new int[N], CRC = new int[N], initR = new int[N];
        int konst, sbuf, mbuf, nbuf;

        static int Rotl(int i, int d) { return (i << d) | (int)((uint)i >> (32 - d)); }
        static int Sbox(int i) { i ^= Rotl(i, 5) | Rotl(i, 7); i ^= Rotl(i, 19) | Rotl(i, 22); return i; }
        static int Sbox2(int i) { i ^= Rotl(i, 7) | Rotl(i, 22); i ^= Rotl(i, 5) | Rotl(i, 19); return i; }

        void Cycle()
        {
            int t = R[12] ^ R[13] ^ konst;
            t = Sbox(t) ^ Rotl(R[0], 1);
            for (int i = 1; i < N; i++) R[i - 1] = R[i];
            R[N - 1] = t;
            t = Sbox2(R[2] ^ R[15]);
            R[0] ^= t;
            sbuf = t ^ R[8] ^ R[12];
        }

        void CrcFunc(int i)
        {
            int t = CRC[0] ^ CRC[2] ^ CRC[15] ^ i;
            for (int j = 1; j < N; j++) CRC[j - 1] = CRC[j];
            CRC[N - 1] = t;
        }

        void MacFunc(int i) { CrcFunc(i); R[KEYP] ^= i; }

        void InitState()
        {
            R[0] = 1; R[1] = 1;
            for (int i = 2; i < N; i++) R[i] = R[i - 1] + R[i - 2];
            konst = INITKONST;
        }

        void AddKey(int k) { R[KEYP] ^= k; }
        void Diffuse() { for (int i = 0; i < FOLD; i++) Cycle(); }

        static int Word(byte[] b, int i) { return (b[i + 3] << 24) | (b[i + 2] << 16) | (b[i + 1] << 8) | b[i]; }

        void LoadKey(byte[] key)
        {
            int i;
            for (i = 0; i < (key.Length & ~0x03); i += 4) { AddKey(Word(key, i)); Cycle(); }
            if (i < key.Length)
            {
                byte[] extra = new byte[4];
                for (int j = 0; i < key.Length; i++) extra[j++] = key[i];
                AddKey(Word(extra, 0));
                Cycle();
            }
            AddKey(key.Length);
            Cycle();
            for (i = 0; i < N; i++) CRC[i] = R[i];
            Diffuse();
            for (i = 0; i < N; i++) R[i] ^= CRC[i];
        }

        public void Key(byte[] key)
        {
            InitState();
            LoadKey(key);
            konst = R[0];
            Array.Copy(R, initR, N);
            nbuf = 0;
        }

        public void Nonce(byte[] nonce)
        {
            Array.Copy(initR, R, N);
            konst = INITKONST;
            LoadKey(nonce);
            konst = R[0];
            nbuf = 0;
        }

        public void Encrypt(byte[] buffer, int offset, int n)
        {
            int i = offset, end = offset + n, t;
            if (nbuf != 0)
            {
                while (nbuf != 0 && i < end)
                {
                    mbuf ^= buffer[i] << (32 - nbuf);
                    buffer[i] = (byte)(buffer[i] ^ ((sbuf >> (32 - nbuf)) & 0xFF));
                    i++; nbuf -= 8;
                }
                if (nbuf != 0) return;
                MacFunc(mbuf);
            }
            int j = i + ((end - i) & ~0x03);
            while (i < j)
            {
                Cycle();
                t = Word(buffer, i);
                MacFunc(t);
                t ^= sbuf;
                buffer[i + 3] = (byte)(t >> 24); buffer[i + 2] = (byte)(t >> 16); buffer[i + 1] = (byte)(t >> 8); buffer[i] = (byte)t;
                i += 4;
            }
            if (i < end)
            {
                Cycle();
                mbuf = 0; nbuf = 32;
                while (nbuf != 0 && i < end)
                {
                    mbuf ^= buffer[i] << (32 - nbuf);
                    buffer[i] = (byte)(buffer[i] ^ ((sbuf >> (32 - nbuf)) & 0xFF));
                    i++; nbuf -= 8;
                }
            }
        }

        public void Decrypt(byte[] buffer, int offset, int n)
        {
            int i = offset, end = offset + n, t;
            if (nbuf != 0)
            {
                while (nbuf != 0 && i < end)
                {
                    buffer[i] = (byte)(buffer[i] ^ ((sbuf >> (32 - nbuf)) & 0xFF));
                    mbuf ^= buffer[i] << (32 - nbuf);
                    i++; nbuf -= 8;
                }
                if (nbuf != 0) return;
                MacFunc(mbuf);
            }
            int j = i + ((end - i) & ~0x03);
            while (i < j)
            {
                Cycle();
                t = Word(buffer, i) ^ sbuf;
                MacFunc(t);
                buffer[i + 3] = (byte)(t >> 24); buffer[i + 2] = (byte)(t >> 16); buffer[i + 1] = (byte)(t >> 8); buffer[i] = (byte)t;
                i += 4;
            }
            if (i < end)
            {
                Cycle();
                mbuf = 0; nbuf = 32;
                while (nbuf != 0 && i < end)
                {
                    buffer[i] = (byte)(buffer[i] ^ ((sbuf >> (32 - nbuf)) & 0xFF));
                    mbuf ^= buffer[i] << (32 - nbuf);
                    i++; nbuf -= 8;
                }
            }
        }

        /// <summary>Produces the 4-byte MAC for everything encrypted/decrypted since the last Nonce().</summary>
        public void Finish(byte[] mac)
        {
            if (nbuf != 0) MacFunc(mbuf);
            Cycle();
            AddKey(INITKONST ^ (nbuf << 3));
            nbuf = 0;
            for (int j = 0; j < N; j++) R[j] ^= CRC[j];
            Diffuse();
            int n = mac.Length, i = 0;
            while (n > 0)
            {
                Cycle();
                if (n >= 4)
                {
                    mac[i + 3] = (byte)(sbuf >> 24); mac[i + 2] = (byte)(sbuf >> 16); mac[i + 1] = (byte)(sbuf >> 8); mac[i] = (byte)sbuf;
                    n -= 4; i += 4;
                }
                else
                {
                    for (int j = 0; j < n; j++) mac[i + j] = (byte)(sbuf >> (i * 8));   // (sic) matches the reference
                    break;
                }
            }
        }
    }
}
