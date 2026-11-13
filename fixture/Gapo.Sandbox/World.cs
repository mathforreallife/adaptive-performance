using System;
using System.Collections.Generic;

using Gapo.Sandbox.Contract;

namespace Gapo.Sandbox
{
    public sealed class World
    {
        public const int Shift = 11;
        public const int Size = 1 << Shift;
        public const int Mask = Size - 1;
        public const float Sea = 62f;
        public const int ChunkShift = 6;
        public const int ChunksPerSide = Size >> ChunkShift;

        public readonly float[] Height = new float[Size * Size];
        public readonly int[] Color = new int[Size * Size];
        public readonly byte[] ShadowBaked = new byte[Size * Size];
        public readonly bool[] ChunkReady = new bool[ChunksPerSide * ChunksPerSide];
        public int[] Detail = new int[1];
        public int DetailMask;
        public int ChunksRemaining;

        public float SunElevationDeg = 34f;
        public float TanSun { get { return (float)Math.Tan(SunElevationDeg * Math.PI / 180.0); } }

        readonly Random _rng;
        readonly Queue<int> _chunkQueue = new Queue<int>();
        int _shadowRow;
        float _shadowTan;

        public World(int seed)
        {
            _rng = new Random(seed);
            GenerateHeights();
            _shadowTan = TanSun;
            BakeShadowRows(0, Size, _shadowTan);
            for (int i = 0; i < ChunkReady.Length; i++) ChunkReady[i] = false;
            ChunksRemaining = ChunkReady.Length;
            SetTextureQuality(TextureQuality.Ultra);
        }

        public static int Index(int x, int y) { return ((y & Mask) << Shift) | (x & Mask); }

        public float HeightAt(float x, float y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            float a = Height[Index(ix, iy)], b = Height[Index(ix + 1, iy)];
            float c = Height[Index(ix, iy + 1)], d = Height[Index(ix + 1, iy + 1)];
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }

        public float SurfaceAt(float x, float y) { return Math.Max(HeightAt(x, y), Sea); }


        void GenerateHeights()
        {
            var h = Height;
            int step = Size;
            float scale = 1f;
            h[0] = 0;
            while (step > 1)
            {
                int half = step / 2;
                for (int y = 0; y < Size; y += step)
                    for (int x = 0; x < Size; x += step)
                    {
                        float avg = (h[Index(x, y)] + h[Index(x + step, y)] + h[Index(x, y + step)] + h[Index(x + step, y + step)]) * 0.25f;
                        h[Index(x + half, y + half)] = avg + Rand() * scale;
                    }
                for (int y = 0; y < Size; y += half)
                    for (int x = (y / half % 2 == 0) ? half : 0; x < Size; x += step)
                    {
                        float avg = (h[Index(x - half, y)] + h[Index(x + half, y)] + h[Index(x, y - half)] + h[Index(x, y + half)]) * 0.25f;
                        h[Index(x, y)] = avg + Rand() * scale;
                    }
                step = half;
                scale *= step > 64 ? 0.62f : 0.5f;
            }

            var tmp = new float[h.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                        tmp[Index(x, y)] = (h[Index(x, y)] * 4 + h[Index(x - 1, y)] + h[Index(x + 1, y)] + h[Index(x, y - 1)] + h[Index(x, y + 1)]) * 0.125f;
                Array.Copy(tmp, h, h.Length);
            }

            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < h.Length; i++) { if (h[i] < min) min = h[i]; if (h[i] > max) max = h[i]; }
            float inv = 1f / (max - min);
            for (int i = 0; i < h.Length; i++)
            {
                float n = (h[i] - min) * inv;
                n = (float)Math.Pow(n, 1.35);
                h[i] = 18f + n * 250f;
            }
        }

        float Rand() { return (float)(_rng.NextDouble() * 2 - 1); }

        public void SetTextureQuality(TextureQuality q)
        {
            int size = q == TextureQuality.Ultra ? 2048 : q == TextureQuality.High ? 1024 : 256;
            if (Detail.Length == size * size) return;
            var rng = new Random(7);
            var d = new int[size * size];
            for (int i = 0; i < d.Length; i++) d[i] = 236 + rng.Next(20);
            Detail = d;
            DetailMask = size - 1;
        }


        public void QueueChunksAround(float cx, float cy)
        {
            var list = new List<KeyValuePair<float, int>>();
            for (int y = 0; y < ChunksPerSide; y++)
                for (int x = 0; x < ChunksPerSide; x++)
                {
                    float wx = (x + 0.5f) * 64 - cx, wy = (y + 0.5f) * 64 - cy;
                    wx -= (float)Math.Round(wx / Size) * Size;
                    wy -= (float)Math.Round(wy / Size) * Size;
                    list.Add(new KeyValuePair<float, int>(wx * wx + wy * wy, y * ChunksPerSide + x));
                }
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            _chunkQueue.Clear();
            foreach (var kv in list) _chunkQueue.Enqueue(kv.Value);
        }

        public int BakeChunks(int count)
        {
            int n = 0;
            while (n < count && _chunkQueue.Count > 0)
            {
                int c = _chunkQueue.Dequeue();
                if (ChunkReady[c]) continue;
                BakeChunk(c % ChunksPerSide, c / ChunksPerSide);
                ChunkReady[c] = true;
                ChunksRemaining--;
                n++;
            }
            return n;
        }

        public void RestartStreaming(float cx, float cy)
        {
            for (int c = 0; c < ChunkReady.Length; c++) ChunkReady[c] = false;
            ChunksRemaining = ChunkReady.Length;
            QueueChunksAround(cx, cy);
        }

        public void BakeStreamingBurst(int newChunks, int workChunks)
        {
            int baked = BakeChunks(newChunks);
            for (int extra = baked; extra < workChunks; extra++)
            {
                _streamingRebakeCursor = (_streamingRebakeCursor + 1) % ChunkReady.Length;
                BakeChunk(_streamingRebakeCursor % ChunksPerSide, _streamingRebakeCursor / ChunksPerSide);
            }
        }

        int _streamingRebakeCursor;

        public void BakeAllChunks()
        {
            for (int c = 0; c < ChunkReady.Length; c++)
                if (!ChunkReady[c]) { BakeChunk(c % ChunksPerSide, c / ChunksPerSide); ChunkReady[c] = true; }
            ChunksRemaining = 0;
            _chunkQueue.Clear();
        }

        public bool IsChunkReady(int idx)
        {
            int cx = (idx & Mask) >> ChunkShift, cy = (idx >> Shift) >> ChunkShift;
            return ChunkReady[cy * ChunksPerSide + cx];
        }

        void BakeChunk(int chx, int chy)
        {
            int x0 = chx << ChunkShift, y0 = chy << ChunkShift;
            for (int y = y0; y < y0 + 64; y++)
                for (int x = x0; x < x0 + 64; x++)
                    Color[Index(x, y)] = ComputeColor(x, y);
        }

        public void RebakeArea(int cx, int cy, int radius)
        {
            for (int y = cy - radius; y <= cy + radius; y++)
                for (int x = cx - radius; x <= cx + radius; x++)
                    Color[Index(x, y)] = ComputeColor(x, y);
            BakeShadowRows(cy - radius - 2, 2 * radius + 5, _shadowTan);
        }

        static float Hash(int x, int y)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }

        float ValueNoise(float x, float y)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int p = 2048 >> 5;
            float a = Hash(ix % p, iy % p), b = Hash((ix + 1) % p, iy % p), c = Hash(ix % p, (iy + 1) % p), d = Hash((ix + 1) % p, (iy + 1) % p);
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }

        int ComputeColor(int x, int y)
        {
            var H = Height;
            float h = H[Index(x, y)];
            float dx = H[Index(x + 1, y)] - H[Index(x - 1, y)];
            float dy = H[Index(x, y + 1)] - H[Index(x, y - 1)];
            float slope = (float)Math.Sqrt(dx * dx + dy * dy) * 0.5f;

            float nx = -dx, ny = -dy, nz = 2.4f;
            float inv = 1f / (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
            float lambert = (nx * 0.72f + ny * 0.18f + nz * 0.67f) * inv;
            if (lambert < 0) lambert = 0;

            float around = (H[Index(x + 3, y)] + H[Index(x - 3, y)] + H[Index(x, y + 3)] + H[Index(x, y - 3)]) * 0.25f;
            float cavity = Clamp01(1f - (around - h) * 0.06f);

            float n1 = ValueNoise(x / 32f, y / 32f);
            float n2 = Hash(x, y);
            float r, g, b;
            if (h < Sea)
            {
                float depth = Clamp01((Sea - h) / 40f);
                r = Lerp(52, 14, depth); g = Lerp(132, 54, depth); b = Lerp(150, 104, depth);
                return Pack(r, g, b);
            }
            if (h < Sea + 3.5f) { r = 212; g = 196; b = 146; }
            else if (slope > 2.6f || (h > 176 && slope > 1.2f)) { float t = n2 * 0.25f; r = 118 + 30 * t; g = 108 + 24 * t; b = 98 + 20 * t; }
            else if (h > 206) { r = 238; g = 242; b = 248; }
            else if (h > 168) { float t = n1; r = Lerp(124, 146, t); g = Lerp(120, 134, t); b = Lerp(98, 112, t); }
            else if (n1 > 0.58f && h < 150) { float t = n2 * 0.3f; r = 44 + 16 * t; g = 92 + 22 * t; b = 46 + 10 * t; }
            else { float t = n1 * 0.7f + n2 * 0.3f; r = Lerp(86, 132, t); g = Lerp(148, 170, t); b = Lerp(62, 78, t); }

            float light = (0.42f + 0.72f * lambert) * (0.75f + 0.25f * cavity);
            return Pack(r * light, g * light, b * light);
        }

        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        public static int Pack(float r, float g, float b)
        {
            int ir = r > 255 ? 255 : (r < 0 ? 0 : (int)r), ig = g > 255 ? 255 : (g < 0 ? 0 : (int)g), ib = b > 255 ? 255 : (b < 0 ? 0 : (int)b);
            return (ir << 16) | (ig << 8) | ib;
        }


        public void BakeShadowRows(int firstRow, int rows, float tanSun)
        {
            var H = Height;
            for (int r = 0; r < rows; r++)
            {
                int y = (firstRow + r) & Mask;
                int rowBase = y << Shift;
                float horizon = -1e9f;
                for (int lap = 0; lap < 2; lap++)
                    for (int x = Mask; x >= 0; x--)
                    {
                        float h = H[rowBase | x];
                        if (h < Sea) h = Sea;
                        horizon -= tanSun;
                        if (lap == 1)
                        {
                            float over = horizon - h;
                            ShadowBaked[rowBase | x] = over <= 0 ? (byte)0 : (byte)(over > 6f ? 255 : (int)(over * 42.5f));
                        }
                        if (h > horizon) horizon = h;
                    }
            }
        }

        public void UpdateBakedShadows(int rowsPerFrame)
        {
            if (_shadowRow == 0) _shadowTan = TanSun;
            BakeShadowRows(_shadowRow, rowsPerFrame, _shadowTan);
            _shadowRow = (_shadowRow + rowsPerFrame) & Mask;
        }
    }
}
