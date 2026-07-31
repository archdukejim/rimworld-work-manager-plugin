// WorkManagerLog.cs
// Copyright (c) 2026 archdukejim

namespace WorkManager;

/// <summary>Prefixed logging, so anything this mod says is obviously ours in a modded log.</summary>
public static class WorkManagerLog
{
    private const string Prefix = "[Work Manager] ";

    private static readonly HashSet<string> OnceKeys = [];

    public static void Message(string message) => Log.Message(Prefix + message);

    public static void Warning(string message) => Log.Warning(Prefix + message);

    public static void Error(string message) => Log.Error(Prefix + message);

    /// <summary>Logs a warning the first time a given key comes up, then stays quiet about it.</summary>
    public static void WarningOnce(string key, string message)
    {
        if (OnceKeys.Add(key))
        {
            Warning(message);
        }
    }
}
