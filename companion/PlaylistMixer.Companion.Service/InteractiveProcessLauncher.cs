using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace PlaylistMixer.Companion.Service;

/// <summary>
/// Launches a GUI process so it appears on the logged-in user's desktop. A LocalSystem service lives
/// in session 0 and cannot show UI there, so when running as a service we grab the active user
/// session's token and CreateProcessAsUser onto "winsta0\default". The active session is NOT always
/// the physical console: over Remote Desktop the console session has no user token, so we fall back to
/// enumerating sessions for the active one. In dev (console run, already in the user's session) a plain
/// Process.Start is enough.
/// </summary>
public static class InteractiveProcessLauncher
{
    /// <summary>Launches <paramref name="exePath"/> with a single argument. Throws on failure.</summary>
    public static void Launch(string exePath, string argument)
    {
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            Process.Start(new ProcessStartInfo(exePath, [argument]) { UseShellExecute = false });
            return;
        }
        LaunchAsActiveUser(exePath, argument);
    }

    private static void LaunchAsActiveUser(string exePath, string argument)
    {
        var userToken = GetActiveUserToken();
        var dupToken = IntPtr.Zero;
        var envBlock = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, TOKEN_TYPE.TokenPrimary, out dupToken))
                throw new InvalidOperationException($"DuplicateTokenEx failed ({Marshal.GetLastWin32Error()}).");

            if (!CreateEnvironmentBlock(out envBlock, dupToken, inherit: false))
                envBlock = IntPtr.Zero; // non-fatal; launch with the parent environment

            // Show the player's window activated (normal, not minimized) so it comes up in the
            // foreground rather than only flashing in the taskbar. Cross-session foreground from a
            // service is still subject to Windows' foreground-stealing rules, but launching into the
            // active session with SW_SHOWNORMAL is the reliable, generic way to ask for the front.
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = SW_SHOWNORMAL,
            };
            // CreateProcess may write to the command-line buffer — pass a mutable StringBuilder.
            var cmd = new StringBuilder($"\"{exePath}\" \"{argument}\"");

            const uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;
            if (!CreateProcessAsUser(dupToken, exePath, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    flags, envBlock, null, ref si, out var pi))
                throw new InvalidOperationException($"CreateProcessAsUser failed ({Marshal.GetLastWin32Error()}).");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            CloseHandle(userToken);
        }
    }

    // Returns the primary token of the logged-on interactive user, or throws if none is available.
    // Prefers the physical console session, then falls back to the first WTSActive session that yields
    // a token — which is what makes launching work when the user is connected over Remote Desktop
    // (the console session exists but has no user token; the RDP session is the active one).
    private static IntPtr GetActiveUserToken()
    {
        var console = WTSGetActiveConsoleSessionId();
        if (console != 0xFFFFFFFF && WTSQueryUserToken(console, out var consoleToken))
            return consoleToken;

        var pInfo = IntPtr.Zero;
        try
        {
            if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out pInfo, out var count))
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(pInfo + i * size);
                    if (info.State == WTS_CONNECTSTATE_CLASS.WTSActive &&
                        WTSQueryUserToken(info.SessionId, out var token))
                        return token;
                }
            }
        }
        finally
        {
            if (pInfo != IntPtr.Zero) WTSFreeMemory(pInfo);
        }

        throw new InvalidOperationException(
            "No interactive user session is available to launch the player. Sign in at the console or a " +
            "Remote Desktop session and try again.");
    }

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const ushort SW_SHOWNORMAL = 1;

    private enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

    // WTS connection states (winsta state of a session). We only care about WTSActive (0).
    private enum WTS_CONNECTSTATE_CLASS { WTSActive, WTSConnected, WTSConnectQuery, WTSShadow, WTSDisconnected, WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO { public uint SessionId; public IntPtr pWinStationName; public WTS_CONNECTSTATE_CLASS State; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int count);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel, TOKEN_TYPE tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? appName, StringBuilder cmdLine,
        IntPtr procAttrs, IntPtr threadAttrs, bool inheritHandles, uint creationFlags, IntPtr env,
        string? currentDir, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION procInfo);
}
