namespace FileManager.Server;

internal static class ApiLog
{
    private static readonly Action<ILogger, Exception?> LogUnhandledException =
        LoggerMessage.Define(LogLevel.Error, new EventId(1000, nameof(UnhandledException)),
            "Unhandled API exception");

    public static void UnhandledException(ILogger logger, Exception exception) =>
        LogUnhandledException(logger, exception);
}
