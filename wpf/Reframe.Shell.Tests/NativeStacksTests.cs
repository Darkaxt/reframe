using System.Runtime.InteropServices;
using Reframe.Shell;
using Xunit;

namespace Reframe.Shell.Tests;

public class NativeStacksTests
{
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [Fact]
    public void A_waiting_threads_stack_goes_from_the_kernel_through_the_runtime()
    {
        using var release = new ManualResetEvent(false);
        using var waiting = new ManualResetEventSlim();
        uint id = 0;
        var thread = new Thread(() =>
        {
            id = GetCurrentThreadId();
            waiting.Set();
            release.WaitOne();
        }) { IsBackground = true };
        thread.Start();
        try
        {
            waiting.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(300);
            var frames = NativeStacks.Read().Single(t => t.Thread == id).Frames;
            var stack = string.Join(" <- ", frames);
            // The wait in the kernel, through the runtime's wait (coreclr), out to managed code.
            Assert.Contains(frames, f => f.StartsWith("ntdll.dll+", StringComparison.OrdinalIgnoreCase));
            Assert.True(frames.Any(f => f.StartsWith("coreclr.dll+", StringComparison.OrdinalIgnoreCase)), stack);
            Assert.True(frames.Count >= 4, stack);
        }
        finally
        {
            release.Set();
            thread.Join();
        }
    }
}
