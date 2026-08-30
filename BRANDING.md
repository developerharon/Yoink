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

## Color

One accent color, used sparingly (primary actions, progress, focus), plus semantic colors for download
outcomes, plus a light/dark neutral scale for backgrounds, text, and borders.

### Accent

| Token | Hex | Use |
|---|---|---|
| Accent | `#6C5DD3` | Primary buttons, progress bar fill, focus/selection accents |
| Accent Light | `#8879E0` | Hover states on accent surfaces |
| Accent Dark | `#5347B0` | Pressed states on accent surfaces |

Violet reads as modern and distinct from the default Fluent blue without clashing with YouTube's red —
this app is a tool that acts *on* video, not a competing brand.

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
| App background | `#F6F6FB` | `#121218` |
| Card background | `#FFFFFF` | `#1B1B23` |
| Border | `#E7E7F0` | `#2A2A34` |
| Text — primary | `#1B1B23` | `#F2F2F5` |
| Text — muted | `#6B6B7B` | `#9A9AA8` |

## Typography

**[Inter](https://rsms.me/inter/)** — already bundled via the `Avalonia.Fonts.Inter` package and enabled
app-wide with `.WithInterFont()` in `Program.cs`, so no font files or licensing to manage. It's designed
for UI text at small sizes, which is most of what this app shows (labels, list rows, form fields).

| Style class | Size | Weight | Use |
|---|---|---|---|
| `AppTitle` | 26px | SemiBold | The "Yoink" wordmark in the header |
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
- Primary actions get `Classes="Primary"` on the `Button`; everything else uses the FluentTheme default
  button look so the primary action stays visually singular per screen.
- Status text/icons for downloads use the semantic colors above (`SuccessBrush`/`ErrorBrush`), never the
  accent color — the accent is reserved for actions and progress, not outcomes.
