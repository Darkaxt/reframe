using System.Runtime.InteropServices;
using Reframe.Shell;
using Xunit;

namespace Reframe.Shell.Tests;

public class NativeStacksTests
{
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [Fact]
    public void A_waiting_threads_stack_has_its_native_frames_and_the_managed_ones_that_called_them()
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
            // The wait in the kernel, through the runtime's wait, out to this test's code.
            Assert.Contains(frames, f => f.StartsWith("ntdll.dll+", StringComparison.OrdinalIgnoreCase));
            Assert.True(frames.Any(f => f.Contains("NativeStacksTests")), stack);
        }
        finally
        {
            release.Set();
            thread.Join();
        }
    }
}
