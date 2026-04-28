namespace TimeToDo.TestIdP;

/// <summary>
/// In-memory log sink used by integration tests to inspect what happened
/// inside the TestIdP during a flow. Cleared and read via <c>Reset</c> and
/// <c>Dump</c>. Thread-safe but intentionally dumb — not for production.
/// </summary>
public static class TestIdpLog
{
    private static readonly List<string> _lines = new();
    private static readonly Lock _lock = new();

    public static void Write(string line)
    {
        lock (_lock) _lines.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {line}");
    }

    public static string[] Dump()
    {
        lock (_lock) return _lines.ToArray();
    }

    public static void Reset()
    {
        lock (_lock) _lines.Clear();
    }
}
