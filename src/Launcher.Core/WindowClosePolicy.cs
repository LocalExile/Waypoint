namespace Launcher.Core;

public enum WindowCloseAction { Ask, Hide, Exit, Wait }

/// <summary>Hiding may keep work running; only a real exit is blocked by an active operation.</summary>
public static class WindowClosePolicy
{
    public static WindowCloseAction Decide(LauncherSettings settings, bool busy, bool explicitExit, bool sessionEnding)
    {
        if (sessionEnding) return WindowCloseAction.Exit;
        if (!explicitExit && !settings.CloseBehaviorConfirmed) return WindowCloseAction.Ask;
        if (!explicitExit && settings.CloseToTray) return WindowCloseAction.Hide;
        return busy ? WindowCloseAction.Wait : WindowCloseAction.Exit;
    }
}
