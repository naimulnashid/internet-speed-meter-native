# Project site

A one-page site that shows what this app is and does, for people who find
the repository: how the meter and the dashboard split the work, the history
on disk, a tour of both pages and the details panel, every chart and readout
the app draws, and every feature.

Plain HTML, CSS and one script, with no framework and no dependencies. Every
number is generated in the browser from a fixed random seed, so it is the same
on every load and is nobody's connection; the live readouts tick once a
second from a random walk. Formats, colours and rules follow the app's own
(`src/SpeedMeter.Core/View/Format.cs`, `src/SpeedMeter.App/Theme/Palette.cs`,
`Meter/MeterPalette.cs`, `Meter/PixelFont.cs`, `Charts/`, `Views/`). The
speed test on the page is simulated: it downloads nothing. The Connection
card shows documentation values only (203.0.113.0/24, AS64500), the same ones
the app's screenshots use.

The app screenshots come from `docs/screenshots/`, which `tools\Snapshot.ps1`
regenerates; the page shows the Speed and Test captures matching its own
theme. The build copies them in, and the app icon as the favicon, rather than
keeping second copies here.

**Phones get the desktop layout, zoomed to fit**: the viewport tag is
`width=1024` with no initial scale, so a phone lays the page out 1024px wide
and scales it down. Desktop browsers ignore the tag.

## Build and preview

```bash
node site/build.mjs
```

writes the site to `site/dist/` (git-ignored). Serve that folder with any
static server to look at it, for example:

```bash
python -m http.server 7954 --bind 127.0.0.1 --directory site/dist
```

## Deploying on Vercel

Import the repository and set **Root Directory** to `site`. Everything else
comes from `site/vercel.json`: no framework, no install step, the build
command and output folder, and the security headers. Vercel's build can read
files outside the root directory, which is how the screenshots and the icon
get in.

**A push builds only when the site could have changed**: something under
`site/`, `docs/screenshots/` or the app icon differs from the last successful
deployment (`ignoreCommand`; exit 0 skips, anything else builds). With no
previous deployment, or one whose commit is not in Vercel's clone, the command
fails and the build runs, so it errs towards building. Any other file the site
starts reading from must be added to that path list.
