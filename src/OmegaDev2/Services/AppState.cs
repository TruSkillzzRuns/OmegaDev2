using System;

namespace OmegaDev2.Services;

/// <summary>
/// Process-wide app state so every page shares the same server URL (users
/// only type it once) and can observe connection changes.
/// </summary>
public static class AppState
{
    private static string _serverUrl = "http://localhost:8080";
    public static string ServerUrl
    {
        get => _serverUrl;
        set { if (_serverUrl != value) { _serverUrl = value; ServerUrlChanged?.Invoke(); } }
    }
    public static event Action? ServerUrlChanged;

    public static bool ServerReachable { get; private set; }
    public static event Action? ServerStatusChanged;

    public static void SetServerReachable(bool reachable)
    {
        if (ServerReachable == reachable) return;
        ServerReachable = reachable;
        ServerStatusChanged?.Invoke();
    }
}
