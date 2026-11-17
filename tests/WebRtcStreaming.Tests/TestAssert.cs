using System.Diagnostics;

namespace WebRtcStreaming.Tests;

internal static class TestAssert
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}; received {actual}.");
    }

    public static void Eventually(Func<bool> condition, string message, int timeoutMs = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException(message);
            Thread.Sleep(5);
        }
    }

    public static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }
}
