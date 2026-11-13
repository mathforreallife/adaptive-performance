using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Gapo.Sandbox.Contract;

namespace Gapo.Sandbox
{
    public sealed class Renderer
    {
        public const int WaterBit = 1 << 30;

        readonly World _world;
        public int OutW, OutH;
        public int RW, RH;
        int[] _color = new int[1], _coord = new int[1], _ytop = new int[1], _final = new int[1];
        float[] _depth = new float[1], _colDirX = new float[1], _colDirY = new float[1], _colFogTint = new float[1];
        int[] _skyRow = new int[1];
        public int[] Output { get { return _final; } }

        public float CamX, CamY, CamH, Yaw;
        float _fx, _fy, _rx, _ry, _scale, _horizon;
        const float TanHalfFov = 1.0f;

        public SandboxSettings Settings = SandboxSettings.UnoptimisedDefaults();
        public float Detail = 1f;
        public float TimeSec;
        ParallelOptions _opts = new ParallelOptions { MaxDegreeOfParallelism = 1 };
        int _threads = 1;
        AntiAliasing _aaMode = (AntiAliasing)(-1);

        public float TerrainMs, ShadowMs, WaterMs, SkyMs, FogMs, SpritesMs, PostMs;

        readonly float[] _cloudTex = new float[256 * 256];
        readonly float[] _fogNoise = new float[256 * 256];
        readonly float[] _expLut = new float[1024];

        public struct Sprite { public float X, Y, Size, Z; public int Color, Kind, Facing; }
        readonly List<Sprite> _sprites = new List<Sprite>(4096);
        int _particleCount;
        float[] _pSX = new float[1], _pSY = new float[1], _pZ = new float[1];

        public Renderer(World world)
        {
            _world = world;
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    _cloudTex[y * 256 + x] = CloudFbm(x / 256f, y / 256f);
                    _fogNoise[y * 256 + x] = Noise(x / 32f, y / 32f, 8, 91);
                }
            for (int i = 0; i < _expLut.Length; i++) _expLut[i] = (float)Math.Exp(-i / 128.0);
        }

        public int Threads
        {
            get { return _threads; }
            set { _threads = Math.Max(1, value); _opts = new ParallelOptions { MaxDegreeOfParallelism = _threads }; }
        }

        public void Resize(int outW, int outH)
        {
            bool ssaa = Settings.AntiAliasing == AntiAliasing.SSAA4x;
            int rw = ssaa ? outW * 2 : outW, rh = ssaa ? outH * 2 : outH;
            if (outW == OutW && outH == OutH && rw == RW && rh == RH && Settings.AntiAliasing == _aaMode) return;
            _aaMode = Settings.AntiAliasing;
            OutW = outW; OutH = outH; RW = rw; RH = rh;
            _color = new int[RW * RH];
            _depth = new float[RW * RH];
            _coord = new int[RW * RH];
            _ytop = new int[RW];
            _colDirX = new float[RW]; _colDirY = new float[RW]; _colFogTint = new float[RW];
            _skyRow = new int[RH];
            _final = (Settings.AntiAliasing == AntiAliasing.Off) ? _color : new int[OutW * OutH];
        }

        void Dispatch(int count, Action<int, int> body)
        {
            if (_threads <= 1 || count < 8) { body(0, count); return; }
            int chunks = Math.Min(count, _threads * 4);
            Parallel.For(0, chunks, _opts, ci => body((int)((long)count * ci / chunks), (int)((long)count * (ci + 1) / chunks)));
        }


        public void Render(IList<Sprite> entitySprites, float[] partX, float[] partY, float[] partZ, int partCount)
        {
            var sw = Stopwatch.StartNew();
            _fx = (float)Math.Cos(Yaw); _fy = (float)Math.Sin(Yaw);
            _rx = -_fy; _ry = _fx;
            _scale = RW * 0.5f / TanHalfFov;
            _horizon = RH * 0.36f;
            for (int x = 0; x < RW; x++)
            {
                float t = ((x + 0.5f) / RW * 2f - 1f) * TanHalfFov;
                _colDirX[x] = _fx + _rx * t;
                _colDirY[x] = _fy + _ry * t;
                float len = (float)Math.Sqrt(_colDirX[x] * _colDirX[x] + _colDirY[x] * _colDirY[x]);
                _colFogTint[x] = Math.Max(0f, _colDirX[x] / len);
            }
            BuildSkyGradient();

            long t0 = sw.ElapsedTicks;
            Dispatch(RW, TerrainColumns);
            long t1 = sw.ElapsedTicks;
            Dispatch(RH, SkyRows);
            long t2 = sw.ElapsedTicks;
            if (Settings.Shadows != ShadowQuality.Off) Dispatch(RH, ShadowRows);
            long t3 = sw.ElapsedTicks;
            if (Settings.WaterReflections != WaterQuality.Off) Dispatch(RH, WaterRows);
            long t4 = sw.ElapsedTicks;
            if (Settings.Fog != FogQuality.Off) Dispatch(RH, FogRows);
            long t5 = sw.ElapsedTicks;
            PrepareSprites(entitySprites, partX, partY, partZ, partCount);
            Dispatch(RH, SpriteRows);
            long t6 = sw.ElapsedTicks;
            if (Settings.AntiAliasing == AntiAliasing.SSAA4x) Dispatch(OutH, ResolveSsaaRows);
            else if (Settings.AntiAliasing == AntiAliasing.FXAA) Dispatch(OutH, FxaaRows);
            long t7 = sw.ElapsedTicks;

            double f = 1000.0 / Stopwatch.Frequency;
            TerrainMs = (float)((t1 - t0) * f);
            SkyMs = (float)((t2 - t1) * f);
            ShadowMs = (float)((t3 - t2) * f);
            WaterMs = (float)((t4 - t3) * f);
            FogMs = (float)((t5 - t4) * f);
            SpritesMs = (float)((t6 - t5) * f);
            PostMs = (float)((t7 - t6) * f + t0 * f);
        }

        void BuildSkyGradient()
        {
            for (int y = 0; y < RH; y++)
            {
                float v = (_horizon - y) / (float)RH;
                float t = Clamp01(v * 2.4f);
                float r = Lerp(214, 64, t), g = Lerp(228, 128, t), b = Lerp(240, 214, t);
                _skyRow[y] = World.Pack(r, g, b);
            }
        }


        void TerrainColumns(int x0, int x1)
        {
            var hm = _world.Height; var cm = _world.Color; var ready = _world.ChunkReady;
            var det = _world.Detail; int dmask = _world.DetailMask; int dshift = Log2(dmask + 1);
            var color = _color; var depth = _depth; var coord = _coord;
            int rw = RW, rh = RH;
            float camX = CamX, camY = CamY, camH = CamH, scale = _scale, horizon = _horizon;
            float maxZ = Settings.RenderDistance;
            bool lod = Settings.TerrainLod;
            const float Sea = World.Sea;

            for (int x = x0; x < x1; x++)
            {
                float dirx = _colDirX[x], diry = _colDirY[x];
                int ytop = rh;
                float z = 1.5f, dz = (lod ? 0.55f : 1f) / Detail;
                while (z < maxZ)
                {
                    int ix = ((int)(camX + dirx * z + 65536f)) & World.Mask;
                    int iy = ((int)(camY + diry * z + 65536f)) & World.Mask;
                    int idx = (iy << World.Shift) | ix;
                    float h = hm[idx];
                    int flag = 0;
                    if (h < Sea) { h = Sea; flag = WaterBit; }
                    int sy = (int)((camH - h) * scale / z + horizon);
                    if (sy < ytop)
                    {
                        int c;
                        if (ready[((iy >> World.ChunkShift) << (World.Shift - World.ChunkShift)) | (ix >> World.ChunkShift)])
                        {
                            c = cm[idx];
                            int d = det[((iy & dmask) << dshift) | (ix & dmask)];
                            c = (((((c >> 16) & 255) * d) >> 8) << 16) | (((((c >> 8) & 255) * d) >> 8) << 8) | (((c & 255) * d) >> 8);
                        }
                        else c = PlaceholderColor(h);
                        int y0 = sy < 0 ? 0 : sy;
                        int p = y0 * rw + x;
                        int v = idx | flag;
                        for (int y = y0; y < ytop; y++, p += rw) { color[p] = c; depth[p] = z; coord[p] = v; }
                        ytop = y0;
                        if (ytop <= 0) break;
                    }
                    z += dz;
                    if (lod) dz += 0.0105f;
                }
                _ytop[x] = ytop;
            }
        }

        static int PlaceholderColor(float h)
        {
            int g = 70 + (int)(h * 0.35f);
            return (g << 16) | (g << 8) | (g + 6 > 255 ? 255 : g + 6);
        }

        static int Log2(int v) { int r = 0; while ((1 << r) < v) r++; return r; }


        void SkyRows(int y0, int y1)
        {
            var color = _color; var depth = _depth; var coord = _coord; var ytop = _ytop;
            int rw = RW;
            float maxZ = Settings.RenderDistance;
            var clouds = Settings.Clouds;
            float wind = TimeSec * 0.006f;
            for (int y = y0; y < y1; y++)
            {
                int sky = _skyRow[y];
                float v = (_horizon - y) / _scale;
                bool cloudRow = clouds != CloudQuality.Off && v > 0.015f;
                float dist = cloudRow ? 520f / v : 0f;
                float fade = cloudRow ? Clamp01((v - 0.015f) * 9f) : 0f;
                int p = y * rw;
                for (int x = 0; x < rw; x++, p++)
                {
                    if (y >= ytop[x]) continue;
                    int c = sky;
                    if (cloudRow)
                    {
                        float u = (CamX + _colDirX[x] * dist) * 0.00055f + wind;
                        float w = (CamY + _colDirY[x] * dist) * 0.00055f;
                        float n = clouds == CloudQuality.Ultra ? CloudFbm(u, w, Detail >= 0.8f ? 5 : 4) : SampleBilinear(_cloudTex, u, w);
                        float cov = Smooth(0.48f, 0.78f, n) * fade;
                        if (cov > 0.003f)
                        {
                            float shade = 0.82f + 0.18f * Clamp01((n - 0.5f) * 3f);
                            c = Mix(c, World.Pack(250 * shade, 250 * shade, 252 * shade), cov * 0.92f);
                        }
                    }
                    color[p] = c; depth[p] = maxZ; coord[p] = -1;
                }
            }
        }

        static float CloudFbm(float u, float v, int octaves = 5)
        {
            float sum = 0, amp = 0.5f, freq = 4f;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Noise(u * freq, v * freq, (int)freq, 17 + o);
                freq *= 2f; amp *= 0.5f;
            }
            return sum / 0.97f;
        }

        static float Noise(float x, float y, int period, int seed)
        {
            float fxl = (float)Math.Floor(x), fyl = (float)Math.Floor(y);
            int ix = (int)fxl, iy = (int)fyl;
            float fx = x - fxl, fy = y - fyl;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int x0 = Mod(ix, period), y0 = Mod(iy, period), x1 = Mod(ix + 1, period), y1 = Mod(iy + 1, period);
            float a = Hash(x0, y0, seed), b = Hash(x1, y0, seed), c = Hash(x0, y1, seed), d = Hash(x1, y1, seed);
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }

        static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }

        static float SampleBilinear(float[] tex, float u, float v)
        {
            float x = u * 256f, y = v * 256f;
            float fxl = (float)Math.Floor(x), fyl = (float)Math.Floor(y);
            int ix = (int)fxl & 255, iy = (int)fyl & 255;
            float fx = x - fxl, fy = y - fyl;
            int ix1 = (ix + 1) & 255, iy1 = (iy + 1) & 255;
            float a = tex[(iy << 8) | ix], b = tex[(iy << 8) | ix1], c = tex[(iy1 << 8) | ix], d = tex[(iy1 << 8) | ix1];
            return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
        }


        void ShadowRows(int y0, int y1)
        {
            var color = _color; var coord = _coord; var hm = _world.Height; var baked = _world.ShadowBaked;
            int rw = RW;
            bool ultra = Settings.Shadows == ShadowQuality.Ultra;
            float tanSun = _world.TanSun;
            int steps = Math.Max(6, (int)(22 * Detail));
            float stepLen = 3f * 22 / steps;
            for (int y = y0; y < y1; y++)
            {
                int p = y * rw;
                for (int x = 0; x < rw; x++, p++)
                {
                    int v = coord[p];
                    if (v < 0 || (v & WaterBit) != 0) continue;
                    float light;
                    if (ultra)
                    {
                        int cx = v & World.Mask, rowBase = v & ~World.Mask;
                        float h = hm[v];
                        light = 1f;
                        for (int k = 1; k <= steps; k++)
                        {
                            float dist = k * stepLen;
                            float over = hm[rowBase | ((cx + (int)dist) & World.Mask)] - h - dist * tanSun;
                            if (over > 0)
                            {
                                float s = 1f - over / (0.9f + k * 0.42f);
                                if (s < light) light = s;
                                if (light <= 0f) { light = 0f; break; }
                            }
                        }
                    }
                    else light = 1f - baked[v] * (1f / 255f);
                    if (light >= 0.999f) continue;
                    color[p] = Scale(color[p], 0.5f + 0.5f * light);
                }
            }
        }


        void WaterRows(int y0, int y1)
        {
            var color = _color; var coord = _coord; var depth = _depth; var hm = _world.Height; var cm = _world.Color;
            int rw = RW;
            bool ultra = Settings.WaterReflections == WaterQuality.Ultra;
            float camH = CamH, t = TimeSec;
            int steps = Math.Max(8, (int)(24 * Detail));
            float kScale = 24f / steps;
            for (int y = y0; y < y1; y++)
            {
                int p = y * rw;
                int mirror = (int)(2 * _horizon - y);
                int skyMirror = _skyRow[mirror < 0 ? 0 : (mirror >= RH ? RH - 1 : mirror)];
                for (int x = 0; x < rw; x++, p++)
                {
                    int v = coord[p];
                    if (v < 0 || (v & WaterBit) == 0) continue;
                    float z = depth[p];
                    float slope = (camH - World.Sea) / z;
                    float fres = Clamp01(0.95f - slope * 1.6f);
                    int refl = skyMirror;
                    if (ultra)
                    {
                        float dx = _colDirX[x], dy = _colDirY[x];
                        float inv = 1f / (float)Math.Sqrt(dx * dx + dy * dy);
                        dx *= inv; dy *= inv;
                        int idx0 = v & ~WaterBit;
                        float wx = (idx0 & World.Mask) + (float)Math.Sin((idx0 >> World.Shift) * 0.21f + t * 1.7f) * 1.2f;
                        float wy = (idx0 >> World.Shift) + (float)Math.Cos((idx0 & World.Mask) * 0.19f + t * 1.3f) * 1.2f;
                        for (int k = 1; k <= steps; k++)
                        {
                            float kk = k * kScale;
                            float dist = kk * 3.5f + kk * kk * 0.2f;
                            int idx = World.Index((int)(wx + dx * dist), (int)(wy + dy * dist));
                            if (hm[idx] > World.Sea + dist * slope)
                            {
                                refl = Scale(cm[idx], 0.8f);
                                break;
                            }
                        }
                    }
                    color[p] = Mix(color[p], refl, fres * 0.75f);
                }
            }
        }


        void FogRows(int y0, int y1)
        {
            var color = _color; var depth = _depth;
            int rw = RW;
            bool vol = Settings.Fog == FogQuality.Volumetric;
            float maxZ = Settings.RenderDistance;
            float k = 3.0f / maxZ;
            float camH = CamH;
            for (int y = y0; y < y1; y++)
            {
                int p = y * rw;
                float vy = (_horizon - y) / _scale;
                for (int x = 0; x < rw; x++, p++)
                {
                    float d = depth[p];
                    float tau;
                    if (vol)
                    {
                        float nx = (CamX + _colDirX[x] * d * 0.5f) * 0.004f + TimeSec * 0.01f, ny = (CamY + _colDirY[x] * d * 0.5f) * 0.004f;
                        float n = SampleBilinear(_fogNoise, nx, ny);
                        tau = 0f;
                        float stepLen = d / 4f;
                        for (int i = 0; i < 4; i++)
                        {
                            float hs = camH + vy * (i + 0.5f) * stepLen;
                            float heightTerm = _expLut[Clamp((int)((hs - 40f) * 1.3f), 0, 1023)];
                            tau += (0.35f + 0.65f * heightTerm) * stepLen;
                        }
                        tau *= k * 0.9f * (0.55f + 0.9f * n);
                    }
                    else tau = d * k;
                    float T = _expLut[Clamp((int)(tau * 128f), 0, 1023)];
                    if (T > 0.995f) continue;
                    float sun = _colFogTint[x];
                    int fog = World.Pack(206 + 30 * sun, 218 + 16 * sun, 232 - 8 * sun);
                    color[p] = Mix(fog, color[p], T);
                }
            }
        }


        void PrepareSprites(IList<Sprite> entities, float[] px, float[] py, float[] pz, int count)
        {
            _sprites.Clear();
            float maxZ = Settings.RenderDistance;
            float sin = _fy, cos = _fx;
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                float dx = e.X - CamX, dy = e.Y - CamY;
                dx -= (float)Math.Round(dx / World.Size) * World.Size;
                dy -= (float)Math.Round(dy / World.Size) * World.Size;
                float z = dx * cos + dy * sin;
                if (z < 3f || z > maxZ * 0.8f) continue;
                float xc = dx * _rx + dy * _ry;
                float sx = RW * 0.5f + xc / z * _scale;
                float size = e.Size * _scale / z;
                if (sx < -size * 2 || sx > RW + size * 2 || size < 0.35f) continue;
                float h = _world.SurfaceAt(e.X, e.Y);
                float sy = (CamH - h) / z * _scale + _horizon;
                var s = e; s.X = sx; s.Y = sy; s.Size = size; s.Z = z;
                _sprites.Add(s);
            }
            if (_pSX.Length < count) { _pSX = new float[count]; _pSY = new float[count]; _pZ = new float[count]; }
            int n = 0;
            for (int i = 0; i < count; i++)
            {
                float dx = px[i] - CamX, dy = py[i] - CamY;
                float z = dx * cos + dy * sin;
                if (z < 2f) continue;
                float xc = dx * _rx + dy * _ry;
                float sx = RW * 0.5f + xc / z * _scale;
                float sy = (CamH - pz[i]) / z * _scale + _horizon;
                if (sx < 0 || sx >= RW - 1 || sy < 0 || sy >= RH - 1) continue;
                _pSX[n] = sx; _pSY[n] = sy; _pZ[n] = z; n++;
            }
            _particleCount = n;
        }

        void SpriteRows(int y0, int y1)
        {
            var color = _color; var depth = _depth;
            int rw = RW;
            for (int i = 0; i < _sprites.Count; i++)
            {
                var s = _sprites[i];
                float rx = Math.Max(0.8f, s.Size), ry = Math.Max(0.6f, s.Size * 0.62f);
                float cy = s.Y - ry;
                int ya = Math.Max(y0, (int)(cy - ry - rx * 0.4f)), yb = Math.Min(y1, (int)(s.Y + 1));
                if (ya >= yb) continue;
                int xa = Math.Max(0, (int)(s.X - rx * 1.5f)), xb = Math.Min(rw, (int)(s.X + rx * 1.5f) + 1);
                float headX = s.X + s.Facing * rx * 0.95f, headY = cy - ry * 0.25f, headR = Math.Max(0.6f, rx * 0.42f);
                float irx = 1f / (rx * rx), iry = 1f / (ry * ry), ihr = 1f / (headR * headR);
                for (int y = ya; y < yb; y++)
                {
                    int p = y * rw + xa;
                    for (int x = xa; x < xb; x++, p++)
                    {
                        if (depth[p] < s.Z - 2f) continue;
                        float dx = x - s.X, dy = y - cy;
                        float e = dx * dx * irx + dy * dy * iry;
                        float hx = x - headX, hy = y - headY;
                        if (e <= 1f)
                        {
                            float lit = 1.05f - 0.35f * Clamp01((dy / ry + 1f) * 0.5f);
                            color[p] = Scale(s.Color, lit);
                            depth[p] = s.Z;
                        }
                        else if (hx * hx * ihr + hy * hy * ihr <= 1f)
                        {
                            color[p] = 0x3A3430;
                            depth[p] = s.Z;
                        }
                    }
                }
            }
            for (int i = 0; i < _particleCount; i++)
            {
                int x = (int)_pSX[i], y = (int)_pSY[i];
                if (y < y0 || y >= y1) continue;
                int p = y * rw + x;
                float z = _pZ[i];
                if (depth[p] < z) continue;
                color[p] = Mix(color[p], 0xFFD3E4, 0.85f);
                if (z < 45f && y + 1 < y1) { color[p + 1] = Mix(color[p + 1], 0xFFC2DA, 0.6f); color[p + rw] = Mix(color[p + rw], 0xFFC2DA, 0.6f); }
            }
        }


        void ResolveSsaaRows(int y0, int y1)
        {
            var src = _color; var dst = _final;
            int rw = RW, ow = OutW;
            for (int y = y0; y < y1; y++)
            {
                int s0 = (y * 2) * rw, s1 = s0 + rw, d = y * ow;
                for (int x = 0; x < ow; x++, d++, s0 += 2, s1 += 2)
                {
                    int a = src[s0], b = src[s0 + 1], c = src[s1], e = src[s1 + 1];
                    int r = (((a >> 16) & 255) + ((b >> 16) & 255) + ((c >> 16) & 255) + ((e >> 16) & 255)) >> 2;
                    int g = (((a >> 8) & 255) + ((b >> 8) & 255) + ((c >> 8) & 255) + ((e >> 8) & 255)) >> 2;
                    int bl = ((a & 255) + (b & 255) + (c & 255) + (e & 255)) >> 2;
                    dst[d] = (r << 16) | (g << 8) | bl;
                }
            }
        }

        void FxaaRows(int y0, int y1)
        {
            var src = _color; var dst = _final;
            int w = OutW, h = OutH;
            for (int y = y0; y < y1; y++)
            {
                int p = y * w;
                for (int x = 0; x < w; x++, p++)
                {
                    int c = src[p];
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1) { dst[p] = c; continue; }
                    int n = src[p - w], s = src[p + w], e = src[p + 1], wv = src[p - 1];
                    int lc = Luma(c), ln = Luma(n), ls = Luma(s), le = Luma(e), lw = Luma(wv);
                    int mx = Math.Max(lc, Math.Max(Math.Max(ln, ls), Math.Max(le, lw)));
                    int mn = Math.Min(lc, Math.Min(Math.Min(ln, ls), Math.Min(le, lw)));
                    if (mx - mn < 24) { dst[p] = c; continue; }
                    int r = (((c >> 16) & 255) * 4 + ((n >> 16) & 255) + ((s >> 16) & 255) + ((e >> 16) & 255) + ((wv >> 16) & 255)) >> 3;
                    int g = (((c >> 8) & 255) * 4 + ((n >> 8) & 255) + ((s >> 8) & 255) + ((e >> 8) & 255) + ((wv >> 8) & 255)) >> 3;
                    int b = ((c & 255) * 4 + (n & 255) + (s & 255) + (e & 255) + (wv & 255)) >> 3;
                    dst[p] = (r << 16) | (g << 8) | b;
                }
            }
        }

        static int Luma(int c) { return (((c >> 16) & 255) * 2 + ((c >> 8) & 255) * 5 + (c & 255)) >> 3; }


        public int PickCell(int outX, int outY)
        {
            int f = RW / Math.Max(1, OutW);
            int x = outX * f, y = outY * f;
            if (x < 0 || y < 0 || x >= RW || y >= RH) return -1;
            int v = _coord[y * RW + x];
            return v < 0 ? -1 : v & ~WaterBit;
        }


        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        static float Smooth(float e0, float e1, float x) { float t = Clamp01((x - e0) / (e1 - e0)); return t * t * (3 - 2 * t); }

        public static int Scale(int c, float f)
        {
            int k = (int)(f * 256f);
            int r = (((c >> 16) & 255) * k) >> 8, g = (((c >> 8) & 255) * k) >> 8, b = ((c & 255) * k) >> 8;
            if (r > 255) r = 255; if (g > 255) g = 255; if (b > 255) b = 255;
            return (r << 16) | (g << 8) | b;
        }

        public static int Mix(int a, int b, float t)
        {
            int k = (int)(t * 256f);
            if (k <= 0) return a; if (k >= 256) return b;
            int ik = 256 - k;
            int r = (((a >> 16) & 255) * ik + ((b >> 16) & 255) * k) >> 8;
            int g = (((a >> 8) & 255) * ik + ((b >> 8) & 255) * k) >> 8;
            int bl = ((a & 255) * ik + (b & 255) * k) >> 8;
            return (r << 16) | (g << 8) | bl;
        }
    }
}
