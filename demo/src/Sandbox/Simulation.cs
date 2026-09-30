using System;
using System.Collections.Generic;
using System.Diagnostics;
using Gapo.Shared;

namespace Gapo.Sandbox
{
    /// <summary>
    /// Main-thread game simulation: flocking creatures with deliberately naive all-pairs AI and
    /// collision (the kind of single-threaded simulation cost the spec's Minecraft example describes),
    /// plus drifting blossom particles.
    /// </summary>
    public sealed class Simulation
    {
        readonly World _world;
        readonly Random _rng = new Random(1234);

        public int Total;
        float[] _x = new float[0], _y = new float[0], _vx = new float[0], _vy = new float[0], _phase = new float[0];
        float[] _nvx = new float[0], _nvy = new float[0];
        int[] _tint = new int[0];
        int[] _active = new int[0];
        public int ActiveCount;
        public int Considered;

        public float EntitiesMs, PhysicsMs, ParticlesMs;

        // Particles
        public float[] PX = new float[0], PY = new float[0], PZ = new float[0];
        float[] _pPhase = new float[0];
        public int ParticleCount;

        public Simulation(World world) { _world = world; }

        // ---------------------------------------------------------------- setup

        public void Populate(int count)
        {
            var xs = new List<float>(); var ys = new List<float>(); var tints = new List<int>();
            int herd = 0;
            int guard = 0;
            while (xs.Count < count && guard++ < 100000)
            {
                // Herds sit next to the autopilot route so they are always on screen.
                float t = (float)(_rng.NextDouble() * CameraPath.LoopSeconds);
                if (herd == 0) t = CameraPath.BenchmarkStart + 2.2f;
                float px, py, yaw;
                CameraPath.Position(t, out px, out py, out yaw);
                float side = (float)(_rng.NextDouble() * 2 - 1) * 70f;
                float fwd = 30f + (float)_rng.NextDouble() * 90f;
                float cx = px + (float)Math.Cos(yaw) * fwd - (float)Math.Sin(yaw) * side;
                float cy = py + (float)Math.Sin(yaw) * fwd + (float)Math.Cos(yaw) * side;
                int size = Math.Min(count - xs.Count, 14 + _rng.Next(26));
                int tint = herd % 5 == 4 ? 0xCFC7BC : (herd % 7 == 3 ? 0x8E8580 : 0xF1EEE6);
                int placed = 0;
                for (int i = 0; i < size * 4 && placed < size; i++)
                {
                    float ex = cx + (float)(_rng.NextDouble() * 2 - 1) * 26f;
                    float ey = cy + (float)(_rng.NextDouble() * 2 - 1) * 26f;
                    if (!Walkable(ex, ey)) continue;
                    xs.Add(Wrap(ex)); ys.Add(Wrap(ey)); tints.Add(tint);
                    placed++;
                }
                herd++;
            }
            Total = xs.Count;
            _x = xs.ToArray(); _y = ys.ToArray(); _tint = tints.ToArray();
            _vx = new float[Total]; _vy = new float[Total]; _phase = new float[Total];
            _nvx = new float[Total]; _nvy = new float[Total]; _active = new int[Total];
            for (int i = 0; i < Total; i++)
            {
                double a = _rng.NextDouble() * Math.PI * 2;
                _vx[i] = (float)Math.Cos(a) * 2f; _vy[i] = (float)Math.Sin(a) * 2f;
                _phase[i] = (float)(_rng.NextDouble() * 100);
            }
        }

        public void SpawnHerd(float cx, float cy, int n)
        {
            int old = Total;
            Array.Resize(ref _x, old + n); Array.Resize(ref _y, old + n); Array.Resize(ref _vx, old + n); Array.Resize(ref _vy, old + n);
            Array.Resize(ref _phase, old + n); Array.Resize(ref _nvx, old + n); Array.Resize(ref _nvy, old + n);
            Array.Resize(ref _tint, old + n); Array.Resize(ref _active, old + n);
            for (int i = old; i < old + n; i++)
            {
                _x[i] = Wrap(cx + (float)(_rng.NextDouble() * 2 - 1) * 12f);
                _y[i] = Wrap(cy + (float)(_rng.NextDouble() * 2 - 1) * 12f);
                double a = _rng.NextDouble() * Math.PI * 2;
                _vx[i] = (float)Math.Cos(a) * 2f; _vy[i] = (float)Math.Sin(a) * 2f;
                _phase[i] = (float)(_rng.NextDouble() * 100);
                _tint[i] = 0xF6E7EF;
            }
            Total = old + n;
        }

        bool Walkable(float x, float y)
        {
            float h = _world.HeightAt(x, y);
            if (h < World.Sea + 1.5f || h > 196f) return false;
            float dx = _world.HeightAt(x + 2, y) - _world.HeightAt(x - 2, y);
            float dy = _world.HeightAt(x, y + 2) - _world.HeightAt(x, y - 2);
            return dx * dx + dy * dy < 30f;
        }

        static float Wrap(float v) { v %= World.Size; return v < 0 ? v + World.Size : v; }

        // ---------------------------------------------------------------- update

        public void Update(float dt, SandboxSettings s, float camX, float camY, float camH)
        {
            var sw = Stopwatch.StartNew();
            int considered = (int)Math.Round(Total * s.EntityDensity / 100.0);
            Considered = considered;
            float radius = s.SimulationDistance == SimulationDistance.Near ? 230f :
                           s.SimulationDistance == SimulationDistance.Far ? 560f : float.MaxValue;
            float r2 = radius * radius;
            int a = 0;
            for (int i = 0; i < considered; i++)
            {
                float dx = Delta(_x[i] - camX), dy = Delta(_y[i] - camY);
                if (dx * dx + dy * dy <= r2) _active[a++] = i;
            }
            ActiveCount = a;

            // Flocking AI: every creature looks at every other simulated creature (O(n²)).
            for (int ii = 0; ii < a; ii++)
            {
                int i = _active[ii];
                float xi = _x[i], yi = _y[i];
                float sepX = 0, sepY = 0, aliX = 0, aliY = 0, cohX = 0, cohY = 0;
                int n = 0;
                for (int jj = 0; jj < a; jj++)
                {
                    int j = _active[jj];
                    if (j == i) continue;
                    float dx = _x[j] - xi, dy = _y[j] - yi;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < 900f)
                    {
                        n++;
                        aliX += _vx[j]; aliY += _vy[j];
                        cohX += dx; cohY += dy;
                        if (d2 < 30f) { float inv = 1f / (d2 + 0.2f); sepX -= dx * inv; sepY -= dy * inv; }
                    }
                }
                float vx = _vx[i], vy = _vy[i];
                if (n > 0)
                {
                    float inv = 1f / n;
                    vx += (aliX * inv - vx) * 0.02f + cohX * inv * 0.004f + sepX * 0.6f;
                    vy += (aliY * inv - vy) * 0.02f + cohY * inv * 0.004f + sepY * 0.6f;
                }
                _phase[i] += dt * 0.7f;
                vx += (float)Math.Cos(_phase[i] * 1.3f + i) * 0.05f;
                vy += (float)Math.Sin(_phase[i] * 1.1f + i * 0.7f) * 0.05f;

                // Keep off the water and away from cliffs.
                float speed = (float)Math.Sqrt(vx * vx + vy * vy) + 1e-4f;
                float ax = xi + vx / speed * 7f, ay = yi + vy / speed * 7f;
                if (!Walkable(ax, ay)) { vx = -vx * 0.8f + vy * 0.4f; vy = -vy * 0.8f - vx * 0.4f; }
                speed = (float)Math.Sqrt(vx * vx + vy * vy) + 1e-4f;
                float maxS = 3.2f, minS = 0.6f;
                if (speed > maxS) { vx *= maxS / speed; vy *= maxS / speed; }
                else if (speed < minS) { vx *= minS / speed; vy *= minS / speed; }
                _nvx[i] = vx; _nvy[i] = vy;
            }
            for (int ii = 0; ii < a; ii++)
            {
                int i = _active[ii];
                _vx[i] = _nvx[i]; _vy[i] = _nvy[i];
                _x[i] = Wrap(_x[i] + _vx[i] * dt);
                _y[i] = Wrap(_y[i] + _vy[i] * dt);
            }
            long t1 = sw.ElapsedTicks;

            // Physics: pairwise collision resolution, repeated for every substep (O(n²) each).
            int steps = s.PhysicsSubsteps;
            for (int step = 0; step < steps; step++)
            {
                for (int ii = 0; ii < a; ii++)
                {
                    int i = _active[ii];
                    for (int jj = ii + 1; jj < a; jj++)
                    {
                        int j = _active[jj];
                        float dx = _x[j] - _x[i], dy = _y[j] - _y[i];
                        float d2 = dx * dx + dy * dy;
                        if (d2 < 16f && d2 > 1e-5f)
                        {
                            float d = (float)Math.Sqrt(d2);
                            float push = (4f - d) * 0.25f / d / steps;
                            _x[i] -= dx * push; _y[i] -= dy * push;
                            _x[j] += dx * push; _y[j] += dy * push;
                        }
                    }
                }
            }
            long t2 = sw.ElapsedTicks;

            UpdateParticles(dt, s, camX, camY, camH);
            long t3 = sw.ElapsedTicks;
            double f = 1000.0 / Stopwatch.Frequency;
            EntitiesMs = (float)(t1 * f);
            PhysicsMs = (float)((t2 - t1) * f);
            ParticlesMs = (float)((t3 - t2) * f);
        }

        static float Delta(float d)
        {
            if (d > World.Size / 2) d -= World.Size; else if (d < -World.Size / 2) d += World.Size;
            return d;
        }

        public static int ParticleTarget(ParticleQuality q)
        {
            switch (q)
            {
                case ParticleQuality.Ultra: return 9000;
                case ParticleQuality.High: return 4500;
                case ParticleQuality.Medium: return 2000;
                case ParticleQuality.Low: return 700;
                default: return 0;
            }
        }

        void UpdateParticles(float dt, SandboxSettings s, float camX, float camY, float camH)
        {
            int target = ParticleTarget(s.Particles);
            if (PX.Length < target)
            {
                int old = PX.Length;
                Array.Resize(ref PX, target); Array.Resize(ref PY, target); Array.Resize(ref PZ, target); Array.Resize(ref _pPhase, target);
                for (int i = old; i < target; i++) Respawn(i, camX, camY, camH, true);
            }
            ParticleCount = target;
            const float Box = 170f;
            var hm = _world.Height;
            for (int i = 0; i < target; i++)
            {
                _pPhase[i] += dt;
                PX[i] += (5.5f + (float)Math.Sin(_pPhase[i] * 1.7f + i) * 3f) * dt;
                PY[i] += ((float)Math.Cos(_pPhase[i] * 1.3f + i * 0.3f) * 3f) * dt;
                PZ[i] -= (2.2f + (i & 7) * 0.25f) * dt;
                float dx = PX[i] - camX, dy = PY[i] - camY;
                if (dx > Box) PX[i] -= 2 * Box; else if (dx < -Box) PX[i] += 2 * Box;
                if (dy > Box) PY[i] -= 2 * Box; else if (dy < -Box) PY[i] += 2 * Box;
                float ground = hm[World.Index((int)PX[i], (int)PY[i])];
                if (ground < World.Sea) ground = World.Sea;
                if (PZ[i] < ground || PZ[i] > camH + 80f) Respawn(i, camX, camY, camH, false);
            }
        }

        void Respawn(int i, float camX, float camY, float camH, bool anyHeight)
        {
            PX[i] = camX + (float)(_rng.NextDouble() * 2 - 1) * 170f;
            PY[i] = camY + (float)(_rng.NextDouble() * 2 - 1) * 170f;
            PZ[i] = anyHeight ? camH - 60f + (float)_rng.NextDouble() * 110f : camH + 20f + (float)_rng.NextDouble() * 40f;
            _pPhase[i] = (float)(_rng.NextDouble() * 10);
        }

        // ---------------------------------------------------------------- output

        public void CollectSprites(List<Renderer.Sprite> list, float yaw)
        {
            list.Clear();
            float rx = -(float)Math.Sin(yaw), ry = (float)Math.Cos(yaw);
            int considered = Considered;
            for (int i = 0; i < considered; i++)
            {
                float side = _vx[i] * rx + _vy[i] * ry;
                list.Add(new Renderer.Sprite { X = _x[i], Y = _y[i], Size = 3.1f, Color = _tint[i], Facing = side >= 0 ? 1 : -1 });
            }
        }

        public byte[] SerializeEntitiesNaively()
        {
            // Text serialisation on the main thread: the sort of thing that makes autosaves hitch.
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Total; i++)
                sb.Append(_x[i].ToString("R")).Append(',').Append(_y[i].ToString("R")).Append(',')
                  .Append(_vx[i].ToString("R")).Append(',').Append(_vy[i].ToString("R")).Append('\n');
            return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        }
    }

    /// <summary>Time-based closed flight path around the island. Deterministic: same t, same view.</summary>
    public static class CameraPath
    {
        public const float LoopSeconds = 150f;
        public const float BenchmarkStart = 18f;

        public static void Position(float t, out float x, out float y, out float yaw)
        {
            double w = 2 * Math.PI / LoopSeconds;
            double r = 640 + 170 * Math.Sin(t * w * 3);
            double th = t * w;
            x = (float)(1024 + r * Math.Cos(th));
            y = (float)(1024 + r * Math.Sin(th));
            // Tangent of the path for the heading.
            double drdt = 170 * 3 * w * Math.Cos(t * w * 3);
            double dx = drdt * Math.Cos(th) - r * w * Math.Sin(th);
            double dy = drdt * Math.Sin(th) + r * w * Math.Cos(th);
            yaw = (float)(Math.Atan2(dy, dx) + 0.28 * Math.Sin(t * 0.11));
        }

        public static float Height(World world, float t)
        {
            float sum = 0, wsum = 0, ground = 0;
            for (int k = -4; k <= 10; k++)
            {
                float x, y, yaw;
                Position(t + k * 0.45f, out x, out y, out yaw);
                float h = world.SurfaceAt(x, y);
                float w = 1f / (1 + Math.Abs(k) * 0.25f);
                sum += h * w; wsum += w;
                if (k >= 0 && k <= 3 && h > ground) ground = h;
            }
            return Math.Max(sum / wsum + 44f, ground + 22f);
        }
    }
}
