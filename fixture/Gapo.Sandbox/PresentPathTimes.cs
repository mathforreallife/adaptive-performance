using System;

namespace Gapo.Sandbox
{
    public sealed class PresentPathTimes
    {
        const int RecentFrames = 300;

        readonly float[] _compose = new float[RecentFrames];
        readonly float[] _upload = new float[RecentFrames];
        readonly float[] _draw = new float[RecentFrames];
        readonly float[] _presentCall = new float[RecentFrames];
        int _count;

        public void Add(float composeMs, float uploadMs, float drawMs, float presentCallMs)
        {
            int slot = _count % RecentFrames;
            _compose[slot] = composeMs;
            _upload[slot] = uploadMs;
            _draw[slot] = drawMs;
            _presentCall[slot] = presentCallMs;
            _count++;
        }

        public double MeanComposeMs { get { return Mean(_compose); } }
        public double MeanUploadMs { get { return Mean(_upload); } }
        public double MeanDrawMs { get { return Mean(_draw); } }
        public double MeanPresentCallMs { get { return Mean(_presentCall); } }

        double Mean(float[] values)
        {
            int count = Math.Min(_count, RecentFrames);
            if (count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < count; i++) sum += values[i];
            return sum / count;
        }
    }
}
