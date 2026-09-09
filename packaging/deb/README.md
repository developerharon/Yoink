# Linux .deb packaging

Not a full Debian source package (no `debuild`/`dpkg-buildpackage`, no `debian/rules`/`changelog`) —
just the handful of files `.github/workflows/release.yml`'s `release-linux` job stages into a
fakeroot tree and builds directly with `dpkg-deb --build --root-owner-group`. That's why this lives
under `packaging/deb/` rather than a top-level `debian/`, which would imply the full toolchain.

- `control.template` — the package metadata. `{{VERSION}}` is substituted from the pushed git tag at
  build time (see the release workflow). No `Depends` on a .NET runtime — the publish this packages
  is `--self-contained true`, so the whole runtime is already bundled. `Recommends: libnotify-bin`
  (not a hard `Depends`) matches `Services/NotificationService.cs`'s own best-effort behavior: it
  already degrades silently with no crash when `notify-send` isn't installed.
- `yoink.desktop` — installed to `/usr/share/applications/`, plus the icon installed to the standard
  hicolor icon theme path, is what makes Yoink show up in the desktop's app launcher/search at all —
  neither existed anywhere in this repo before this packaging was added.
- `postinst`/`postrm` — best-effort `gtk-update-icon-cache`/`update-desktop-database` refresh calls,
  `|| true` throughout so a minimal container/chroot install missing either tool never fails.

Install layout: everything from the self-contained publish output lands under `/opt/yoink/` (the
FHS-sanctioned spot for a bundle that ships its own runtime — same shape Chrome/VS Code/Slack use),
symlinked from `/usr/bin/yoink`.

## Building and testing locally

```sh
dotnet publish Yoink/Yoink.csproj -c Release -r linux-x64 -o publish --self-contained true -p:Version=0.0.0-local

VERSION=0.0.0-local
PKGROOT=pkgroot
mkdir -p "$PKGROOT/opt/yoink" && cp -r publish/* "$PKGROOT/opt/yoink/" && chmod +x "$PKGROOT/opt/yoink/Yoink"
mkdir -p "$PKGROOT/usr/bin" && ln -s /opt/yoink/Yoink "$PKGROOT/usr/bin/yoink"
mkdir -p "$PKGROOT/usr/share/applications" && cp packaging/deb/yoink.desktop "$PKGROOT/usr/share/applications/"
mkdir -p "$PKGROOT/usr/share/icons/hicolor/256x256/apps" && cp Yoink/Assets/app-icons/app-icon-blue.png "$PKGROOT/usr/share/icons/hicolor/256x256/apps/yoink.png"
mkdir -p "$PKGROOT/DEBIAN"
sed "s/{{VERSION}}/$VERSION/" packaging/deb/control.template > "$PKGROOT/DEBIAN/control"
cp packaging/deb/postinst packaging/deb/postrm "$PKGROOT/DEBIAN/" && chmod 755 "$PKGROOT/DEBIAN/postinst" "$PKGROOT/DEBIAN/postrm"

dpkg-deb --build --root-owner-group pkgroot "yoink_${VERSION}_amd64.deb"
```

Then, on a real or containerized Ubuntu:

```sh
sudo apt install ./yoink_0.0.0-local_amd64.deb   # pulls in libnotify-bin via Recommends
yoink                                            # or launch "Yoink" from the app launcher/search
sudo apt remove yoink                            # confirm the .desktop entry/icon are cleaned up too
```

Worth checking for real, not just assuming — this is genuinely untested against a real/clean install
at the time this packaging was added: `ldd /opt/yoink/Yoink` for any missing native `.so` dependency
now that Velopack isn't handling that, and that the app actually appears in the launcher's search
(the hicolor icon cache / desktop database refresh above is what makes that happen).
