# Yoink — Brand Basics

## The mark

A "Y" drawn as a simple line — two arms meeting in a V, one stem, with a
small hook-flick at the base instead of a flat terminal — set inside an
outlined rounded square, stroke-only, no fill. Same weight on the frame and
the letter, generous internal padding, rounded joins throughout. It's built
to sit on any color: white line on an accent-colored square, or an
accent-colored line on a light or dark neutral surface — same shape either
way, just swap which side carries the color.

The little flick at the bottom of the stem is the one personal touch — a
quiet nod to the hook/snag idea from earlier passes, kept small enough that
it doesn't fight the clean, outlined read.

## Files

```
icons/
  yoink-mark-mono.svg          Icon only, fill="none" stroke="currentColor" —
                                 drop in anywhere, color it via CSS `color`.

wordmarks/
  yoink-wordmark-color.svg     Badge + "Yoink" in Yoink Blue — light backgrounds.
  yoink-wordmark-white.svg     All-white — dark or colored backgrounds.
  yoink-wordmark-ink.svg       All-neutral-ink — light backgrounds, no accent.

app-icons/
  yoink-badge-{blue,orange,purple,green,red}.svg   Outlined badge per accent
                                                       (white line on accent fill).
  yoink-badge-light-surface.svg / -dark-surface.svg  Accent-colored line on a
                                                       neutral surface, for OS chrome.
  yoink-badge-blue-compact.svg   Thicker stroke, simplified Y (no flick) — used
                                   to generate the smallest raster sizes, where
                                   the regular stroke weight gets lost.
  yoink-icon-{16,24,32,48,64,128,256,512}.png   Raster exports. 16/24/32 are
                                                   rendered from the compact
                                                   variant; 48 and up use the
                                                   regular badge.
  yoink.ico                    16/32/48 ICO for a Windows build, if you ever ship one.

tokens/
  tokens.css / tokens.json     Unchanged — same neutrals and five accents as before.

yoink-brand-showcase.html      Open in a browser — toggle light/dark and accent,
                                 and see the mark on every background at once.
```

## Color

Unchanged.

| Accent | Hex |
|---|---|
| Yoink Blue | `#2F6FED` |
| Ubuntu Orange | `#E95420` |
| Grab Purple | `#8B5CF6` |
| Snatch Green | `#22A06B` |
| Heist Red | `#E5484D` |

| Neutral | Light | Dark |
|---|---|---|
| `bg` | `#F7F8FA` | `#0F1115` |
| `surface` | `#FFFFFF` | `#171A21` |
| `surface-2` | `#EEF0F3` | `#1F232C` |
| `border` | `#DCE0E5` | `#2E3340` |
| `text-primary` | `#1A1D23` | `#F0F2F5` |
| `text-secondary` | `#6B7280` | `#9AA1AC` |

## Usage notes

- Below 32px, use the raster exports (built from the compact variant) rather
  than scaling the regular SVG down — the outline stroke gets too thin and
  anti-aliases into mush otherwise.
- Frame and letter are always the same stroke weight and the same color.
  Don't make the frame thinner than the Y, or vice versa — that's what breaks
  the Facebook-style read this was built to match.
- Keep the fill behind the mark solid. The outline needs consistent contrast
  against its background — no gradients or photos behind it.
- Don't add a second color to the mark itself (e.g. colored frame + different
  colored Y). One color, on one background, every time.
