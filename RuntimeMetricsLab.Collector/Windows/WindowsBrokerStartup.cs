using System.ComponentModel;
using RuntimeMetricsLab.Contracts.Attribution;

namespace RuntimeMetricsLab.Collector.Windows;

public static class WindowsBrokerStartup
{
    public static Task ListenBeforeReady(Func<Task> beginListening, Action ready)
    {
        var connection = beginListening();
        if (connection.IsCompleted) connection.GetAwaiter().GetResult();
        ready();
        return connection;
    }

    public static WindowsBrokerStartupFailure Failure(WindowsProfilerActivation identity,
        WindowsBrokerStartupStage stage, Exception exception)
    {
        int? nativeError = exception is Win32Exception native ? native.NativeErrorCode :
            ((uint)exception.HResult & 0xffff0000) == 0x80070000 ? exception.HResult & 0xffff : null;
        var message = exception.Message;
        if (message.Length > 1024) message = message[..1024] + " (see collector log for full details)";
        return new(identity, stage, nativeError, exception.HResult, message);
    }

    public static string Describe(WindowsBrokerStartupFailure failure)
    {
        if (!Enum.IsDefined(failure.Stage) || string.IsNullOrWhiteSpace(failure.Message) || failure.Message.Length > 1200)
            throw new ArgumentException("Invalid collector startup failure report.");
        return $"Windows collector startup failed during {failure.Stage}: {failure.Message} " +
            (failure.NativeErrorCode is { } code ? $"(Win32 {code}; HRESULT 0x{failure.HResult:x8})." : $"(HRESULT 0x{failure.HResult:x8}).");
    }
}
