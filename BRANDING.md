# Yoink branding

Design tokens and visual guidelines for the app. This is the source of truth for colors, type, and
spacing — when adding UI, reuse the resources/style classes described here instead of hand-rolling new
ones, so the app stays visually consistent as it grows past a single window.

The tokens below are implemented as Avalonia resources in `Yoink/App.axaml` (`Application.Resources` for
colors/brushes, `Application.Styles` for the reusable style classes). Read that file alongside this one —
this doc explains *why* the values are what they are; the XAML is the enforced version.

## Name & feel

**Yoink** — grab a video, keep it. The tone is quick, a little playful, but the UI itself should read as
clean and competent rather than cutesy: think a focused utility app, not a toy.

## The mark

An outlined rounded square with a "Y" drawn as a simple line inside it — one stroke weight throughout, no
fill, built to sit on any background (an accent-colored square with a white line, or a light/dark neutral
square with an accent-colored line — same shape, whichever side carries the color). It shows up in the app
in two different forms, both derived from the same source shape but built for a different job:

- **The window/taskbar/tray icon** — a full-color square (accent fill, white line), one raster PNG per
  accent preset at `Assets/app-icons/app-icon-{blue,orange,purple,green,red}.png`. `App.ApplyAccent` picks
  the one matching the current `AppSettings.AccentColor` and applies it to every open `Window` plus the
  tray icon, so the taskbar icon itself matches whichever mood the user picked — not just in-app buttons.
  These PNGs are a generated artifact, not hand-drawn: rendered at 256×256 from the exact same
  rect/path coordinates as `Assets/brand/badges/yoink-badge-*.svg` (rounded rect `rx=18`, stroke width 7,
  scaled) via a one-off SkiaSharp console script — regenerate them the same way if the mark's geometry ever
  changes (rect at `(14,14)`-`(86,86)` with `rx=18`, path
  `M 34,32 L 50,50 L 66,32 M 50,50 L 50,64 Q 50,71 43,71`, both stroked at width 7 in a 100×100 viewBox,
  `#FAFBFC` line on the accent fill).
- **The nav bar mark** next to "Yoink" in `Views.MainWindow`'s `FANavigationView.PaneCustomContent` (NOT
  `PaneHeader` — verified via an actual rendered frame, not just XAML compiling, that FluentAvaloniaUI
  3.1.0's top-nav mode never presents `PaneHeader`/`PaneTitle` at all; `PaneCustomContent` is the slot that
  renders on the left in that mode) — drawn
  natively as an Avalonia `Path` (`Classes="YoinkMark"`, geometry in `App.axaml`'s `YoinkMarkGeometry`
  resource) rather than a raster image, so it stays crisp at any size and recolors live via
  `{DynamicResource AccentBrush}` exactly like every other accent-colored surface in the app — no image
  asset or SVG-rendering package needed for this one. The rounded-rect frame is re-expressed as an
  arc-based path (Avalonia's `Path` mini-language has no rounded-rect shorthand); keep it in sync with
  `yoink-mark-mono.svg` if the mark's shape ever changes. Sized down (22px, `StrokeThickness="2"`) and set
  next to a plain 16px `SemiBold` "Yoink" `TextBlock` rather than the `AppTitle` style class — the nav
  bar's top strip is compact chrome, not a page heading, so it doesn't want `AppTitle`'s 26px size; see
  "Applying this to new UI" below and the `ui-navigation-shell` project memory for the rest of the shell.

Two of the source badges (`yoink-badge-green.svg`/`-red.svg` under `Assets/brand/badges/`) aren't part of
the delivered asset set — hand-authored here by copying the blue/orange/purple pattern with the matching
accent hex, to keep the five-preset app-icon set complete. `yoink-wordmark-color.svg` and the
light/dark-surface neutral badge variants are staged under `Assets/brand/` for reference but not consumed
by the running app.

## Color

One **accent** color, used sparingly (primary actions, progress, focus) — but unlike everything else on
this page, the accent is user-configurable, not fixed. Plus semantic colors for download outcomes, plus a
light/dark neutral scale for backgrounds, text, and borders (both fixed).

### Accent — five presets, user-chosen

Five presets ship; the user picks one in `Views.SettingsWindow` ("pick whatever matches your mood") and it
applies app-wide immediately via `App.ApplyAccent` — see that method's doc comment for exactly how. Each
preset carries a base/hover/active/soft (a tint for progress tracks and subtle backgrounds — see
`App.ApplyAccent` for how it differs between light and dark mode)/on-accent (always white — all five are
dark enough to hold white text at AA contrast) set, persisted as `AppSettings.AccentColor`.

| Preset | Base | Hover | Active | Soft |
|---|---|---|---|---|
| Yoink Blue (default) | `#2F6FED` | `#275DC7` | `#1F4AA0` | `#E4ECFD` |
| Ubuntu Orange | `#E95420` | `#C6431A` | `#A23716` | `#FDEAE2` |
| Grab Purple | `#8B5CF6` | `#7C3AED` | `#6D28D9` | `#F1EAFE` |
| Snatch Green | `#22A06B` | `#1C8659` | `#166B47` | `#E0F5EC` |
| Heist Red | `#E5484D` | `#CB3439` | `#A82A2E` | `#FBE4E5` |

These values, the neutral scale below, and the mark/badge SVGs all come from a small external
design-tokens package staged under `Yoink/Assets/brand/` (`tokens.css`/`tokens.json` are the same data in
web/JSON form — kept for reference and for regenerating this table, not consumed by the app directly, since
`App.axaml`'s `Color`/`SolidColorBrush` resources are the enforced version here; `yoink-brand-showcase.html`
is a standalone live preview you can just open in a browser). See "The mark" above for exactly how the
badge SVGs turn into the actual app icon, and the mono mark into the header logomark.

Don't add a sixth preset without also adding a matching swatch button in `Views.SettingsWindow` and a case
in `App.ApplyAccent`'s palette table.

### Semantic (download status)

| Token | Hex | Use |
|---|---|---|
| Success | `#22B573` | Completed downloads |
| Error | `#E5484D` | Failed downloads, error dialogs |
| Warning | `#F2A93B` | Reserved for future use (e.g. retry/partial states) |

### Neutrals

Two full scales, one per theme variant — never hardcode a neutral color in a view; always go through the
`AppBackgroundBrush` / `CardBackgroundBrush` / `BorderBrush2` / `TextMutedBrush` resources so light/dark
switching (see the main README/CLAUDE.md for the theme feature) stays correct automatically.

| Token | Light | Dark |
|---|---|---|
| App background | `#F7F8FA` | `#0F1115` |
| Card background | `#FFFFFF` | `#171A21` |
| Border | `#DCE0E5` | `#2E3340` |
| Text — primary | `#1A1D23` | `#F0F2F5` |
| Text — muted | `#6B7280` | `#9AA1AC` |

Text — primary isn't its own Avalonia resource (no `TextPrimaryBrush`) — it's close enough to FluentTheme's
own default text color in each variant that nothing currently overrides it. Only wire up a dedicated
resource for it if that ever visibly drifts.

## Typography

**[Inter](https://rsms.me/inter/)** — already bundled via the `Avalonia.Fonts.Inter` package and enabled
app-wide with `.WithInterFont()` in `Program.cs`, so no font files or licensing to manage. It's designed
for UI text at small sizes, which is most of what this app shows (labels, list rows, form fields).

| Style class | Size | Weight | Use |
|---|---|---|---|
| `AppTitle` | 26px | SemiBold | A page's own heading (e.g. "Settings" in `SettingsView`) — not the nav bar's compact wordmark, see "The mark" above |
| `Subtitle` | 13px | Regular, muted | Tagline under the wordmark |
| `SectionTitle` | 15px | SemiBold | Card headings ("Download a video", "Recent downloads") |
| (default) | 13px | Regular | Body text, labels, list rows |
| `Caption` | 12px, muted | Regular | Secondary metadata (timestamps, resolution) |

## Layout

- **Spacing scale**: multiples of 4px — 4, 8, 12, 16, 20, 24. Card padding is 20px; gaps between stacked
  elements are 12px; gaps between major sections are 20px.
- **Corner radius**: 8px on controls (buttons, text boxes, combo boxes), 14px on cards.
- **Elevation**: flat, not skeuomorphic — a 1px border (`BorderBrush2`) plus a soft, barely-visible drop
  shadow on cards. No heavy shadows, no gradients beyond the accent color itself.

## Applying this to new UI

- Wrap a logical group of controls in a `Border` with `Classes="Card"` rather than a bare `StackPanel`.
- Use `TextBlock` style classes (`AppTitle`/`Subtitle`/`SectionTitle`/`Caption`) instead of one-off
  `FontSize`/`FontWeight` setters.
- Primary actions get `Classes="Primary"` on the `Button`; everything else uses FluentAvaloniaTheme's
  default button look so the primary action stays visually singular per screen. (`FluentAvaloniaTheme`
  replaced vanilla Avalonia's `<FluentTheme/>` in `App.axaml` for the navigation shell — see the
  `ui-navigation-shell` project memory — but it's a superset covering the same controls, so nothing above
  changed because of it.)
- A settings-style page (grouped rows, each with a label/description on the left and one control on the
  right) should use `FASettingsExpander`/`FASettingsExpanderItem` (see `Views.SettingsView`) rather than
  hand-rolled `DockPanel` rows — it's the idiom FluentAvaloniaUI ships for exactly this, and keeps new
  settings visually consistent with the existing ones without re-deriving row spacing/typography by hand.
- Status text/icons for downloads use the semantic colors above (`SuccessBrush`/`ErrorBrush`), never the
  accent color — the accent is reserved for actions and progress, not outcomes.
- Any new accent-colored surface should go through `AccentBrush`/`AccentSoftBrush`/`OnAccentBrush`
  (`DynamicResource`, not `StaticResource` — see `App.ApplyAccent`) so it repaints correctly when the user
  changes their accent preset, rather than a literal hex.
