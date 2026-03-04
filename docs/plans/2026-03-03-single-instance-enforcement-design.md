# Single-Instance Enforcement Design

## Summary

Prevent multiple instances of the Sendspin Windows client from running simultaneously. When a second instance is launched, it signals the first instance to show its window, then exits silently.

## Motivation

Currently, launching the app when it's already running in the system tray creates a completely separate process with its own audio pipeline, server discovery, tray icon, and clock synchronizer. This confuses users and wastes system resources.

## Design

### Approach: Named Mutex + Named Pipe

**Named Mutex** (`Sendspin_SingleInstance`) detects whether an instance is already running. **Named Pipe** (`Sendspin_ShowWindow`) provides cross-process signaling to bring the existing window to the foreground.

### New File: `src/SendspinClient/SingleInstanceGuard.cs`

An `IDisposable` class with two roles:

**First instance (owns the mutex):**
1. Creates the named Mutex — `createdNew` is `true`
2. Starts a `NamedPipeServerStream` listener on a background task
3. When a connection arrives, raises `ShowWindowRequested` event
4. Loops: after each connection, creates a new pipe server to listen again

**Second instance (mutex already exists):**
1. Tries to create the mutex — `createdNew` is `false`
2. Connects to the named pipe, sends a short message
3. `TryStart()` returns `false` — caller knows to shut down

### Modified File: `src/SendspinClient/App.xaml.cs`

At the top of `OnStartup`, before DI/logging/window creation:

```csharp
_singleInstanceGuard = new SingleInstanceGuard();
if (!_singleInstanceGuard.TryStart())
{
    Shutdown();
    return;
}
_singleInstanceGuard.ShowWindowRequested += (_, _) =>
    Dispatcher.Invoke(() => { /* Show + Activate MainWindow */ });
```

Guard is disposed in `OnExit` (releases mutex, stops pipe server).

### Window Activation

When `ShowWindowRequested` fires, dispatch to UI thread:
- `MainWindow.Show()` — unhides from tray
- `MainWindow.WindowState = WindowState.Normal` — restores if minimized
- `MainWindow.Activate()` — brings to foreground

### Edge Cases

| Scenario | Behavior |
|----------|----------|
| First instance crashes | OS releases kernel mutex; next launch becomes the first instance |
| First instance exits between mutex check and pipe connect | Pipe connect fails; second instance proceeds as first |
| Multiple rapid launches | Pipe server loops, handles each connection sequentially |
| Pipe connect timeout | 2-second timeout; if exceeded, second instance exits without showing (first instance is likely shutting down) |

### No Breaking Changes

- No new dependencies
- No config changes
- No UI changes
- No public API changes
