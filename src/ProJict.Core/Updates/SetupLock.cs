namespace ProJict.Core.Updates;

/// <summary>
/// ProJict's installer holds a named mutex while it runs (Inno Setup's SetupMutex in installer\ProJict.iss): nothing may
/// start ProJict meanwhile, or its files stay in use and the update fails ("Access is denied"). Setup starts ProJict
/// itself when it's done, with <see cref="AfterSetupFlag"/>. ProJictBridge.exe checks the same name.
/// </summary>
public static class SetupLock
{
    public const string Name = "ProJictSetupRunning";
    /// <summary>ProJict started by Setup at the end: it may start although Setup still holds the lock for a moment.</summary>
    public const string AfterSetupFlag = "--after-setup";

    public static bool IsHeld(string name = Name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }
}
