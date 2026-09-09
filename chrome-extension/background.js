// Yoink's Chrome extension — MVP scope is deliberately just a right-click context menu (see the
// project's own planning notes: no automatic chrome.downloads interception, no injected YouTube
// button). Every catch here is something the user explicitly clicked, mirroring how the desktop
// app's own clipboard auto-catch never queues anything without a confirmation dialog either — this
// extension only ever *offers* a URL to Yoink; Yoink's own AddDownloadDialog still requires the user
// to confirm before anything is actually queued.

// Must match Yoink/Services/NativeMessagingHost.cs's HostName constant exactly, and the manifest
// this same key/ID pair gets registered under (Yoink self-registers it on first launch — see that
// class's own doc comment for why the app does this rather than a manual setup step).
const NATIVE_HOST_NAME = "com.developerharon.yoink";

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: "yoink-link",
    title: "Download with Yoink",
    contexts: ["link"],
  });

  chrome.contextMenus.create({
    id: "yoink-page",
    title: "Download with Yoink",
    contexts: ["page"],
  });
});

chrome.contextMenus.onClicked.addListener((info) => {
  const url = info.menuItemId === "yoink-link" ? info.linkUrl : info.pageUrl;
  if (!url) return;

  chrome.runtime.sendNativeMessage(NATIVE_HOST_NAME, { url }, (response) => {
    if (chrome.runtime.lastError) {
      // Most common cause: the native host manifest isn't registered yet — Yoink registers it
      // itself the first time it's launched (Linux only, for now), so simply starting Yoink once
      // fixes this. Logged rather than surfaced as a popup — this extension has no UI beyond the
      // context menu.
      console.error("Yoink native messaging failed:", chrome.runtime.lastError.message);
      return;
    }

    if (response?.status === "error")
      console.error("Yoink reported an error:", response.message);
  });
});
