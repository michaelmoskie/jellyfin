using System;
using System.Linq;

namespace MediaBrowser.Controller.MediaEncoding
{
    /// <summary>
    /// Manages round-robin allocation of NVIDIA GPU devices for encoding.
    /// </summary>
    public class NvidiaGpuAllocator
    {
        private readonly int[] _gpuDevices;
        private readonly object _lock = new object();
        private int _currentIndex;

        /// <summary>
        /// Initializes a new instance of the <see cref="NvidiaGpuAllocator"/> class.
        /// </summary>
        /// <param name="deviceList">Comma-separated list of GPU device indices (e.g., "0,1,2").</param>
        public NvidiaGpuAllocator(string deviceList)
        {
            if (string.IsNullOrWhiteSpace(deviceList))
            {
                _gpuDevices = new[] { 0 };
            }
            else
            {
                _gpuDevices = deviceList.Split(',')
                    .Select(s => int.TryParse(s.Trim(), out int idx) && idx >= 0 ? (int?)idx : null)
                    .Where(idx => idx.HasValue)
                    .Select(idx => idx!.Value)
                    .Distinct()
                    .ToArray();

                if (_gpuDevices.Length == 0)
                {
                    _gpuDevices = new[] { 0 };
                }
            }
        }

        /// <summary>
        /// Gets the number of GPUs in the pool.
        /// </summary>
        public int GpuCount => _gpuDevices.Length;

        /// <summary>
        /// Gets the next GPU device index using round-robin selection.
        /// </summary>
        /// <returns>GPU device index.</returns>
        public int GetNextGpu()
        {
            lock (_lock)
            {
                int device = _gpuDevices[_currentIndex];
                _currentIndex = (_currentIndex + 1) % _gpuDevices.Length;
                return device;
            }
        }
    }
}
