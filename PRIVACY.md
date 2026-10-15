# Privacy Notice

**Sendspin for Windows** · Last updated: October 15, 2026 · Applies to version 2.2.5 and later

Sendspin for Windows has no accounts, telemetry, analytics, advertising, or crash reporting. Its developer receives no data from it. The app talks to the Music Assistant servers on your own network. The only feature that sends anything to a third party is **Discord Rich Presence**, which is **off by default**.

## Discord Rich Presence

### What is shared with Discord

While a track is **playing**, the app shares:

- the track **title**
- the **artist**
- the **album** name
- the time the track started, worked out from the playback position, so Discord can show elapsed time

The Discord status also shows a fixed Sendspin logo. No album artwork, server names, network addresses, or account details are sent. When playback pauses or stops, or the feature is turned off, the app clears the status.

### Off by default, and how to turn it on or off

Discord Rich Presence is disabled when you install the app. To turn it on or off, open **Settings** and use the **Discord Rich Presence** toggle ("Show now playing in Discord activity status"). Turning it off clears your Discord status straight away. The same setting is stored as `Discord:Enabled` in the settings file described below.

Nothing is sent unless the Discord desktop app is running on the same PC.

### Discord processes this information

The app hands this information to the Discord desktop app on your PC over Discord's local connection. Discord then processes it as an external service: it shows it on your profile and handles it under the [Discord Privacy Policy](https://discord.com/privacy) and your Discord privacy settings. The developer of Sendspin for Windows does not receive this information.

### What the app itself keeps

The app holds the current track details in memory only, to update your status. It does not save them for this feature. The one exception is logging: file logging is off by default, but if you turn it on at a detailed level (Information or Debug), log lines can include track titles and artists. See "Data stored on your PC" below.

## Data stored on your PC

Everything below stays on your computer and is never uploaded. It lives in `%LOCALAPPDATA%\Sendspin for Windows`.

- **Settings** (`appsettings.json`): your preferences, the player name, the chosen audio device, and the IDs of servers you have connected to.
- **Logs** (`logs` folder): **off by default**. If you turn on file logging, logs are kept to five files of 1 MB each by default. At detailed levels they can include track details, server names, and local network addresses.
- **Sync diagnostics** (`logs\sync-health.log`, versions 2.2.x): timing measurements only, with no track information. The file is limited to about 2 MB.
- **Pairing data** (from version 3.0): an encryption key that identifies this player to your Music Assistant servers, and a record of the servers it is paired with.

Uninstalling removes the program but leaves this folder in place. Delete the folder to remove all of the data above.

## Your local network

- **Music Assistant:** the app connects to the Music Assistant servers on your network. It sends them its player name, volume and mute state, supported audio formats, and timing information. It receives audio, track details, and artwork. It downloads artwork from the address the server provides, normally the server itself.
- **Discovery:** the app uses mDNS on your local network. In the default mode it announces itself, with its player name, so servers can find it. In the alternative mode it looks for servers instead.

This traffic goes only to your own servers.

## Windows features

- **Media controls** ("Hardware Media Keys", on by default): the app gives the current track's title, artist, album, and artwork to Windows, which shows them in its media controls. Turn this off in **Settings**.
- **Notifications** ("Show Notifications", on by default): toast notifications show the current track and connection changes. Windows keeps them in its notification history according to your Windows settings. Turn this off in **Settings**.

## Changes and contact

Changes to this notice are published in this file, and its history is visible in the repository. Questions can be raised at [github.com/chrisuthe/windowsSpin/issues](https://github.com/chrisuthe/windowsSpin/issues).
