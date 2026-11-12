using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Gapo.Sandbox
{
    public sealed unsafe class MemoryBallast : IDisposable
    {
        const int BlockMb = 16;
        const int BlockBytes = BlockMb * 1024 * 1024;
        const int PageBytes = 4096;

        readonly List<IntPtr> _blocks = new List<IntPtr>();
        long _touchCursor;

        public int CommittedMb { get { return _blocks.Count * BlockMb; } }

        public void Resize(int targetMb)
        {
            int targetBlocks = Math.Max(0, targetMb) / BlockMb;
            while (_blocks.Count > targetBlocks)
            {
                NativeMemory.Free((void*)_blocks[_blocks.Count - 1]);
                _blocks.RemoveAt(_blocks.Count - 1);
            }

            while (_blocks.Count < targetBlocks)
            {
                byte* block = (byte*)NativeMemory.Alloc((nuint)BlockBytes);
                for (int offset = 0; offset < BlockBytes; offset += PageBytes) block[offset] = 1;
                _blocks.Add((IntPtr)block);
            }
        }

        public void TouchRolling(int megabytes)
        {
            if (_blocks.Count == 0) return;
            long totalPages = (long)_blocks.Count * (BlockBytes / PageBytes);
            long pagesToTouch = Math.Min(totalPages, (long)megabytes * 1024 * 1024 / PageBytes);
            for (long i = 0; i < pagesToTouch; i++)
            {
                long page = (_touchCursor + i) % totalPages;
                byte* block = (byte*)_blocks[(int)(page / (BlockBytes / PageBytes))];
                block[(page % (BlockBytes / PageBytes)) * PageBytes]++;
            }

            _touchCursor = (_touchCursor + pagesToTouch) % totalPages;
        }

        public void Dispose()
        {
            Resize(0);
        }
    }
}
