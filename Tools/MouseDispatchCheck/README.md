# Mouse dispatch regression check

Run from the repository root:

```powershell
dotnet run --project Tools/MouseDispatchCheck/MouseDispatchCheck.csproj -c Release
```

Links the real `MouseHook`, `ChordGate` and input marker sources. It invokes the hook's
private dispatcher using synthetic Win32 event memory, without installing hooks,
reading preferences, moving the cursor or sending input to Windows. Only the WinUI
coordinate type and settings enum are declared locally to keep the check independent
of the application/UI build.

The 41 assertions cover middle-button, both side-button and both chord triggers:
a declined DOWN must have a forwarded UP even if the grey handoff ends between them;
an accepted DOWN must retain its swallowed UP if triggering becomes unavailable;
ordinary accepted gestures still fire exactly once and keep the first chord button balanced.
