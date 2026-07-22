# Demo videos for the lecture — GPU work, visually

Short external videos to play during the talk. Each entry: where it fits in
`LECTURE-MATERIAL.md`, what it shows, and how much of it to play. Playback logistics at the
bottom.

Rule of thumb: no clip longer than ~2 minutes mid-talk. The videos illustrate one concept and
hand the room back to you.

---

## 1. Mythbusters: GPU vs CPU paintball demo (NVIDIA) — ★ top pick

- **Link:** https://www.youtube.com/watch?v=-P28LKWTzrI (NVIDIA channel) —
  mirror: https://www.youtube.com/watch?v=0udMBdo0Rac
- **Fits:** slide 3 (the GPU wall — "it *can* draw tens of millions of primitives") or as the
  cold open of Act II / slide 9 (GPU 101).
- **Shows:** Adam Savage and Jamie Hyneman paint a smiley face with a single-barrel paintball
  robot (a CPU: one operation at a time), then fire a 1,100-barrel robot that paints the Mona
  Lisa in one 80 ms volley (a GPU: everything in parallel). The single best 90-second intuition
  pump for "why the GPU is the wall that opens" ever filmed.
- **Play:** the whole thing (~1:30). Land the punchline yourself: "our job is to arrive at that
  machine with the paint already loaded in the right barrels — that's the tile format."

## 2. Branch Education — How do Video Game Graphics Work?

- **Link:** https://www.youtube.com/watch?v=C8YtdC8mxTU
- **Fits:** slide 9 (GPU 101: vertex/fragment shader, rasterization).
- **Shows:** a beautifully animated walk through the render pipeline — vertices positioned by
  the vertex shader, rasterization into pixels, fragment shading for color. Exactly the
  vocabulary slide 9 defines, but animated.
- **Play:** ~20 min total — far too long to play whole. Use the video's chapter markers and play
  only the vertex-shading → rasterization → fragment-shading stretch (~2–3 min). Then point back
  at your slide: "we use precisely these two shaders and nothing else."

## 3. Sebastian Lague — Coding Adventure: Ant and Slime Simulations

- **Link:** https://www.youtube.com/watch?v=gQD0vXhAjNA
- **Fits:** end of slide 9, as the "what parallelism buys you" payoff; also works in Act I as
  pure motivation.
- **Shows:** a compute shader driving around a million agents in real time, producing organic
  slime-mold patterns. Visually stunning, and the point lands instantly: per-item work that
  would freeze a CPU for seconds is a single GPU dispatch.
- **Play:** ~1–2 min of the million-agent slime section (mid-video; jump via the seek-bar
  chapters). Frame it as "this is per-*agent* simulation; we only need per-*vertex* math — our
  problem is embarrassingly parallel by comparison."

## 4. Ear clipping / polygon triangulation visualizations

- **Links:** https://www.youtube.com/watch?v=QAdfkylpYwc (overview of ear clipping) —
  alternative with holes: https://www.youtube.com/watch?v=mw8aLh_lPoo
- **Fits:** slide 10 (tessellation offline).
- **Shows:** a polygon being triangulated ear by ear — the exact class of algorithm (earcut) the
  build runs offline. Watching the algorithm chew through vertices one at a time *is* the
  argument for never doing this on the main thread.
- **Play:** ~30–60 s of the animation, muted, while you narrate: "this, times a few million
  rings, is what we refuse to do in your browser tab. We do it once, offline, and ship the
  triangle indices as a tile column."

## 5. kepler.gl live demo — Shan He (Uber) meetup talk

- **Link:** https://www.youtube.com/watch?v=y-SA6bOv4Eo — shorter alternative:
  https://www.youtube.com/watch?v=i2fRN4e2s0A
- **Fits:** slide 4 (the deck.gl/kepler.gl row of the comparison table) or slide 12 (GPU-side
  filtering).
- **Shows:** live demos of millions of GPS points rendered and filtered in the browser with
  deck.gl's GPU filter — brushing a time range and watching millions of points respond
  instantly. It's the strongest public demo of the "filter = uniforms, not refetch" idea slide
  12 makes.
- **Play:** cue up one live-demo segment (~1–2 min) in advance; it's a talk recording, so
  scrub to a demo moment and clip it. Frame: "superb renderer — the row in our table it loses
  on is that the data must fit the tab. Act III is how we remove that ceiling."

## 6. 3Blue1Brown — Hilbert's Curve

- **Link:** https://www.youtube.com/watch?v=3s7h2MHQtxc
- **Fits:** slide 14 (rows Hilbert-sorted within tiles).
- **Shows:** the construction of the Hilbert space-filling curve — the animation makes
  "spatially close ⇒ close on the curve ⇒ close in the file" obvious without any math.
- **Play:** the first ~2 min (the curve construction). Skip the rest (it goes into
  infinite-math philosophy). One sentence after: "we sort every tile's rows along this curve —
  locality for free, for everything downstream."

---

## 0. The best video: record your own engine

The pre-talk checklist already asks for a recolor sequence for slides 16–17. Upgrade it to a
30-second screen recording instead of stills:

- Full zoom-out → drag a date-range filter → watch every feature recolor live, with the
  browser's FPS meter or DevTools performance overlay visible in frame.
- Optionally a second capture of the Network panel staying *empty* during filter/recolor
  interactions — that clip is slide 12 and slide 21's whole argument in one shot.
- Record with OS-native capture (or OBS) at the venue projector's resolution; keep the file
  local in the deck folder. No network dependency, no ads, and it demos *your* GPU work, not
  someone else's.

---

## Playback logistics

**Simplest (recommended): pre-loaded browser tabs.**
- One tab per video, opened and paused at the start point *before* the talk (gets past ads and
  the quality ramp-up; manually set 1080p via the gear icon).
- Deep-link to a timestamp with `&t=95s` on the watch URL so a reload lands where you want.
- During the talk: Alt/Cmd-Tab to the tab, `f` for fullscreen, `k` to play/pause, `←/→` to
  scrub 5 s, `m` to mute. Esc exits fullscreen back to the deck.

**Embedded in the deck (if the deck tool allows):**
- PowerPoint: Insert → Video → Online Video, paste the YouTube URL — plays inline (needs
  internet at the venue).
- Google Slides: Insert → Video (YouTube built in), then Format options → set start/end time —
  the clean way to play only a segment.
- reveal.js / HTML deck: iframe `https://www.youtube.com/embed/<id>?start=95&end=180` — the
  `start`/`end` params clip the segment for you.

**Offline safety net.**
- Venue Wi-Fi is the most common demo failure. For each clip you truly depend on, have an
  offline fallback: YouTube Premium's offline download in the app, or a screen recording of
  just the segment you play. Keep fallbacks in one folder with the deck.
- Your own engine capture (section 0) should always be a local file.

**Segment cueing.**
- YouTube's "Clip" feature (under a video's Share menu) makes a shareable ≤60 s loop — handy
  for the triangulation and slime clips.
- Test every clip on the actual presentation machine + projector + audio before the talk; the
  Mythbusters clip needs sound, the triangulation clip plays better muted under narration.
