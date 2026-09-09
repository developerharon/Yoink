# Yoink Chrome extension

An unpacked/sideloaded Chrome extension (no Chrome Web Store listing yet — see the project's own
planning notes for why). Adds one thing: right-click a link, or right-click the page itself, and
choose **"Download with Yoink"**. That URL is handed to the desktop app, which comes to the front
(launching itself first, if it wasn't already running) with the same "add download" confirmation
dialog every other catch mechanism in the app uses — nothing is ever queued without you confirming
it there.

This is deliberately just a context menu for now — it does not intercept Chrome's own file downloads
automatically, and it does not add anything to YouTube's page itself.

## Install (one-time)

1. Open `chrome://extensions` and turn on **Developer mode** (top-right toggle).
2. Click **Load unpacked** and select this `chrome-extension/` folder.
3. Make sure Yoink itself has been launched at least once on this machine — it self-registers the
   native messaging host it needs (Linux only, for now) the first time it starts, so a right-click
   won't have anything to talk to until that's happened once.

That's it — no separate native-host setup step is needed. The extension's ID is fixed (a signing key
is pinned in `manifest.json`), so it stays the same across reloads/reinstalls of the unpacked folder,
and it already matches what Yoink registers as an allowed caller.

## How it works

- `manifest.json` pins a public key so Chrome always derives the same extension ID
  (`fploffppjmjodgebmdmebocepnkoiefg`) for this folder, no matter how many times it's reloaded.
- `background.js` registers the two context menu items and, on a click, calls
  `chrome.runtime.sendNativeMessage("com.developerharon.yoink", { url }, ...)`.
- Chrome looks up that host name in a manifest file Yoink writes to
  `~/.config/google-chrome/NativeMessagingHosts/` (and the Chromium equivalent) the first time it
  runs, pointing back at Yoink's own executable.
- Yoink, launched that way (`--native-messaging-host`), either forwards the URL to an
  already-running instance over a local IPC channel, or cold-starts itself with the URL ready to go.

See `Yoink/Services/NativeMessagingHost.cs` and `Yoink/Services/SingleInstanceIpcService.cs` in the
main project for the desktop-side half of this bridge.

## If it's not working

- Confirm Yoink has actually been launched at least once since this was added — the native
  messaging host manifest doesn't exist until then.
- Check `~/.config/google-chrome/NativeMessagingHosts/com.developerharon.yoink.json` (or the
  `chromium` equivalent) exists and its `path` points at a real, executable Yoink binary.
- Open the service worker's console from `chrome://extensions` (the "service worker" link under this
  extension, once it's loaded) to see the logged error from `background.js`.
