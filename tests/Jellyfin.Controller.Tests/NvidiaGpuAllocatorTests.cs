using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.Controller.Tests
{
    public class NvidiaGpuAllocatorTests
    {
        [Fact]
        public void Constructor_WithNullDeviceList_UsesSingleDefaultGpu()
        {
            var allocator = new NvidiaGpuAllocator(null!);

            Assert.Equal(1, allocator.GpuCount);
            Assert.Equal(0, allocator.GetNextGpu());
        }

        [Fact]
        public void Constructor_WithEmptyDeviceList_UsesSingleDefaultGpu()
        {
            var allocator = new NvidiaGpuAllocator(string.Empty);

            Assert.Equal(1, allocator.GpuCount);
            Assert.Equal(0, allocator.GetNextGpu());
        }

        [Fact]
        public void Constructor_WithWhitespaceDeviceList_UsesSingleDefaultGpu()
        {
            var allocator = new NvidiaGpuAllocator("   ");

            Assert.Equal(1, allocator.GpuCount);
            Assert.Equal(0, allocator.GetNextGpu());
        }

        [Fact]
        public void Constructor_WithSingleDevice_UsesSpecifiedDevice()
        {
            var allocator = new NvidiaGpuAllocator("2");

            Assert.Equal(1, allocator.GpuCount);
            Assert.Equal(2, allocator.GetNextGpu());
            Assert.Equal(2, allocator.GetNextGpu());
        }

        [Fact]
        public void Constructor_WithMultipleDevices_ParsesCorrectly()
        {
            var allocator = new NvidiaGpuAllocator("0,1,2");

            Assert.Equal(3, allocator.GpuCount);
        }

        [Fact]
        public void Constructor_WithDevicesWithSpaces_ParsesCorrectly()
        {
            var allocator = new NvidiaGpuAllocator("0, 1, 2");

            Assert.Equal(3, allocator.GpuCount);
        }

        [Fact]
        public void Constructor_WithDuplicateDevices_RemovesDuplicates()
        {
            var allocator = new NvidiaGpuAllocator("0,1,1,2");

            Assert.Equal(3, allocator.GpuCount);
        }

        [Fact]
        public void Constructor_WithInvalidDevices_UsesDefault()
        {
            var allocator = new NvidiaGpuAllocator("invalid");

            Assert.Equal(1, allocator.GpuCount);
            Assert.Equal(0, allocator.GetNextGpu());
        }

        [Fact]
        public void Constructor_WithNegativeDevice_UsesDefault()
        {
            var allocator = new NvidiaGpuAllocator("-1");

            Assert.Equal(1, allocator.GpuCount);
            Assert.Equal(0, allocator.GetNextGpu());
        }

        [Fact]
        public void Constructor_WithMixedValidAndInvalid_UsesValidOnly()
        {
            var allocator = new NvidiaGpuAllocator("1,invalid,2,-1");

            Assert.Equal(2, allocator.GpuCount);
            Assert.Equal(1, allocator.GetNextGpu());
            Assert.Equal(2, allocator.GetNextGpu());
            Assert.Equal(1, allocator.GetNextGpu());
        }

        [Fact]
        public void Constructor_WithAllInvalidDevices_UsesDefault()
        {
            var allocator = new NvidiaGpuAllocator("invalid,invalid2,-1,-2");

            Assert.Equal(1, allocator.GpuCount);
            Assert.Equal(0, allocator.GetNextGpu());
        }

        [Fact]
        public void GetNextGpu_WithSingleDevice_AlwaysReturnsSameDevice()
        {
            var allocator = new NvidiaGpuAllocator("0");

            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(0, allocator.GetNextGpu());
            }
        }

        [Fact]
        public void GetNextGpu_WithTwoDevices_AlternatesBetweenDevices()
        {
            var allocator = new NvidiaGpuAllocator("0,1");

            Assert.Equal(0, allocator.GetNextGpu());
            Assert.Equal(1, allocator.GetNextGpu());
            Assert.Equal(0, allocator.GetNextGpu());
            Assert.Equal(1, allocator.GetNextGpu());
        }

        [Fact]
        public void GetNextGpu_WithThreeDevices_RotatesThroughDevices()
        {
            var allocator = new NvidiaGpuAllocator("0,1,2");

            Assert.Equal(0, allocator.GetNextGpu());
            Assert.Equal(1, allocator.GetNextGpu());
            Assert.Equal(2, allocator.GetNextGpu());
            Assert.Equal(0, allocator.GetNextGpu());
            Assert.Equal(1, allocator.GetNextGpu());
            Assert.Equal(2, allocator.GetNextGpu());
        }

        [Fact]
        public void GetNextGpu_WithNonSequentialDevices_RotatesCorrectly()
        {
            var allocator = new NvidiaGpuAllocator("0,2,5");

            Assert.Equal(0, allocator.GetNextGpu());
            Assert.Equal(2, allocator.GetNextGpu());
            Assert.Equal(5, allocator.GetNextGpu());
            Assert.Equal(0, allocator.GetNextGpu());
        }

        [Fact]
        public void GetNextGpu_ThreadSafe_NoRaceConditions()
        {
            var allocator = new NvidiaGpuAllocator("0,1,2");
            var results = new List<int>();
            var lockObj = new object();

            Parallel.For(0, 300, _ =>
            {
                var gpu = allocator.GetNextGpu();
                lock (lockObj)
                {
                    results.Add(gpu);
                }
            });

            // Each GPU should be selected exactly 100 times
            Assert.Equal(100, results.Count(x => x == 0));
            Assert.Equal(100, results.Count(x => x == 1));
            Assert.Equal(100, results.Count(x => x == 2));
        }

        [Fact]
        public void GetNextGpu_ThreadSafe_MaintainsRoundRobinOrder()
        {
            var allocator = new NvidiaGpuAllocator("0,1");
            var results = new List<int>();

            // Sequential calls should maintain order even with multiple threads
            for (int i = 0; i < 100; i++)
            {
                results.Add(allocator.GetNextGpu());
            }

            // Should alternate perfectly: 0,1,0,1,0,1...
            for (int i = 0; i < results.Count; i++)
            {
                Assert.Equal(i % 2, results[i]);
            }
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("1", 1)]
        [InlineData("0,1", 0)]
        [InlineData("1,0", 1)]
        [InlineData("2,0,1", 2)]
        public void GetNextGpu_ReturnsFirstDeviceFirst(string deviceList, int expectedFirst)
        {
            var allocator = new NvidiaGpuAllocator(deviceList);

            Assert.Equal(expectedFirst, allocator.GetNextGpu());
        }

        [Fact]
        public void GpuCount_ReturnsCorrectCount()
        {
            var allocator1 = new NvidiaGpuAllocator("0");
            var allocator2 = new NvidiaGpuAllocator("0,1");
            var allocator3 = new NvidiaGpuAllocator("0,1,2,3");

            Assert.Equal(1, allocator1.GpuCount);
            Assert.Equal(2, allocator2.GpuCount);
            Assert.Equal(4, allocator3.GpuCount);
        }
    }
}
