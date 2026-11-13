# DiskLens landing page - design notes

## Brief

- **Subject:** DiskLens, a local disk analyser, a desktop app for Windows and macOS.
- **Audience:** someone whose C: drive is full and who does not know why.
- **Job:** download the installer (Windows) or build it (macOS); learn it is a real app, not a browser tab, and that it updates itself.
- **Constraints:** one self-contained HTML file, shipped inside the app and openable
  offline. No framework, no CDN at runtime (the font link degrades to system fonts).

## Concept card

```
Idea:        The page is the product: a real drive, drawn as the treemap the app draws.
Metaphor:    An atlas. You start at the whole drive and drill until the culprit has a name.
Signature:   The hero treemap re-tiles as you scroll, drilling C: -> Users\sachi ->
             AppData\Local, every block in true proportion to a measured byte count.
Emotion:     Recognition. "That is my disk, and that block is the problem."
Motion verb: Re-tile. Blocks travel to their new rectangle and settle; nothing fades in.
Screenshot:  The first frame: 952.7 GB as nine blocks, AppData the size of a wall.
```

**Anchor:** the numbers are this machine's, measured by the app itself - 3,240,377 files,
499,181 folders, 48 s cold, 2.7 s warm, 31.1 GB of duplicates. A competitor could copy the
layout but not the data, and the whole page falls apart without it.

Alternatives considered: an instrument-bench world (already carried by the Fig 1 figure, so
it would repeat itself) and a cartography/contour treatment (pretty, but it illustrates the
name rather than the product).

**Genericness test:** a generic model lands on a centered hero, a gradient blob and three
feature cards. The treemap hero, the drill tied to scroll and the legend of real folder names
are the parts that answer "no".

## System

- **Palette:** ground `#07070a`, panel `#121218`, ink `#f4f4f7`, muted `#9a9aa8`,
  line `#2a2a35`, and the loud trio lime `#c6f135`, violet `#6b4bff`, cyan `#16d9cf`.
  Verdict colours match the app exactly, so the page and the product agree.
- **Type:** Space Grotesk 500/600/700, system fallback. Display `clamp(3rem, 9vw, 8rem)`,
  tracking -0.05em, leading 0.88. Body 16-17px at 1.5. Numerals tabular everywhere.
- **Grid:** 12 columns, 16px gutter, 1180px max. Section rhythm alternates:
  full-bleed stage, 7/5 split, 12-wide figure, 3-up bento, 6/6 install.
- **Shape:** 18px on cards, 13px on blocks and buttons, 99px on chips only.
- **Motion:** one easing family `cubic-bezier(.32,.72,0,1)`, one clock (a single rAF ticker
  that also drives the Fig 1 figure), transforms and opacity only. Idle: the treemap keeps
  breathing between two orderings at rest, so the concept never stops.
- **Reduced motion:** the drill becomes three static states you scroll past, the figure holds
  one frame, nothing else moves.
