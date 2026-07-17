import { useEffect, useState, useSyncExternalStore } from 'react';
import type { Manifest } from '../lib/manifest';
import { foldRouteOverride } from '../lib/remoteFold';
import { unregisterTileCache } from '../lib/swClient';
import {
  type CacheGauge,
  clearPerf,
  cumulative,
  deckSnapshot,
  frameSamples,
  gpuName,
  LONG_FRAME_MS,
  perfEvents,
  perfStore,
  startFrameProbe,
  stats,
  type StageStat,
  totals,
  type PerfStage,
} from '../lib/perf';
import { btn } from './controlStyles';

/** The live post-bake lifecycle monitor (?perf=1). Every number here is measured in the real pipeline
 *  during real interaction — pan the map and the stages, bytes, and frames below move with it. Renders
 *  off the perf store's throttled notification (~4Hz), never per event: at pan rates a per-event render
 *  would make the dashboard the dominant cost in the frames it is reporting on. */
export default function PerfDashboard({
  manifest,
  tilesInView,
  cache,
}: {
  manifest: Manifest | null;
  tilesInView: number;
  cache: CacheGauge;
}) {
  useSyncExternalStore(perfStore.subscribe, perfStore.getSnapshot);
  const [open, setOpen] = useState(true);

  // The rAF sampler runs only while this panel is mounted — a permanent one would keep the compositor
  // awake and skew the idle-time predictive prefetch it is meant to observe.
  useEffect(startFrameProbe, []);

  const events = perfEvents();
  const frames = frameSamples();
  const st = stats(events);
  const t = totals(events, frames);
  const c = cumulative();
  const gpu = deckSnapshot();

  if (!open)
    return (
      <button style={{ ...shell, ...collapsed }} onClick={() => setOpen(true)}>
        ▲ perf
      </button>
    );

  return (
    <div style={shell}>
      <div style={head}>
        <span style={{ fontWeight: 700 }}>perf</span>
        <span style={{ opacity: 0.55, fontSize: 10, flex: 1 }}>{manifest ? `${manifest.view.id} · ${manifest.version}` : '—'}</span>
        <button style={mini} onClick={() => clearPerf()} title="Reset the window — pan somewhere, clear, then measure the thing you care about">
          clear
        </button>
        {/* The disk cache is cache-first, so once warm it answers every tile forever and a cold load
            becomes unmeasurable. This drops the SW and its caches, then reloads uncontrolled. */}
        <button
          style={mini}
          title="Drop the service worker + tile disk caches and reload — measures a genuinely cold load"
          onClick={() => {
            void unregisterTileCache().then(() => window.location.reload());
          }}
        >
          cold
        </button>
        <button style={mini} onClick={() => setOpen(false)}>
          ▼
        </button>
      </div>

      <Section title="frames">
        <FrameGraph frames={frames} />
        <div style={row}>
          <span>{t.fps ? `${t.fps.toFixed(0)} fps` : '—'}</span>
          <span style={{ color: t.longFrames ? 'var(--warn)' : 'var(--good)' }}>
            {t.longFrames} long (≥{LONG_FRAME_MS}ms)
          </span>
          <span style={{ opacity: 0.6 }}>max {frames.length ? Math.max(...frames).toFixed(0) : 0}ms</span>
        </div>
      </Section>

      {/* Never evicted — grows for the whole session. The window below answers "what is happening now";
          this answers "what has this session cost", which is the number that only ever goes up. */}
      <Section title="session total · since load/clear">
        <Line label="network" v={bytes(c.wire)} note={`↓ ${c.tileReqs + c.factsReqs + c.foldReqs} req`} />
        <Line label="from cache" v={bytes(c.cache)} note={`⚡ saved`} />
        {/* The two disk caches, split. sw.js registers on production builds only, so `sw` reading 0 in dev
            is correct rather than broken — and a non-zero value is the proof it engaged. */}
        <Line label=" · http disk" v={bytes(c.cacheHttp)} note="browser" />
        <Line label=" · sw disk" v={bytes(c.cacheSw)} note={c.cacheSw > 0 ? 'sw.js' : 'prod only'} />
        <Line label=" · tiles" v={bytes(c.tileWire)} note={`${bytes(c.tileCache)} ⚡`} />
        <Line label=" · facts" v={bytes(c.factsWire)} note={`${bytes(c.factsCache)} ⚡`} />
        {c.foldReqs > 0 && <Line label=" · fold" v={bytes(c.foldWire)} note={`${c.foldReqs} req`} />}
        <Line
          label="cache hit"
          v={c.wire + c.cache > 0 ? `${((c.cache / (c.wire + c.cache)) * 100).toFixed(0)}%` : '—'}
          note="of bytes"
        />
      </Section>

      {/* The in-memory tile cache — the memory number that actually governs this app, not the JS heap.
          Evictions climbing under a steady camera is thrash: ground being decoded, dropped, and decoded
          again, which every byte and timing figure above would report as ordinary work. */}
      <Section title="tile cache · ram">
        <Line label="resident" v={bytes(cache.resident)} note={`${cache.tiles} tiles`} />
        <Line
          label="of budget"
          v={`${((cache.resident / cache.budget) * 100).toFixed(0)}%`}
          note={bytes(cache.budget)}
        />
        <Line label="evictions" v={String(cache.evictions)} note={cache.evictions > 0 ? 'thrash?' : 'none'} />
      </Section>

      {/* deck's own once-per-second gauges. bufferMemory is GPU residency measured at the source — the
          bytes deck is holding on the card — rather than inferred from what we uploaded. */}
      <Section title={`gpu · ${gpuName() ?? 'deck'}`}>
        {gpu ? (
          <>
            <Line label="buffers" v={bytes(gpu.bufferMemory)} note="resident" />
            <Line label="textures" v={bytes(gpu.textureMemory)} note="resident" />
            <Line label="gpu/frame" v={`${gpu.gpuTimePerFrame.toFixed(2)} ms`} note="deck" />
            <Line label="cpu/frame" v={`${gpu.cpuTimePerFrame.toFixed(2)} ms`} note="deck" />
            {/* The layer-admission cost: `layers` above only times building the layer objects, which is
                ~0ms — this is deck committing their attributes to the GPU, where the hitch actually is. */}
            <Line label="attr upload" v={`${gpu.updateAttributesTime.toFixed(1)} ms`} note="admission" />
            <Line label="layers" v={`${gpu.drawLayersCount}/${gpu.layersCount}`} note="drawn/all" />
          </>
        ) : (
          <div style={sub}>waiting for deck (publishes once a second)</div>
        )}
      </Section>

      {/* Wire vs decoded, cold vs warm, over the ROLLING WINDOW — the last 4000 events. Deliberately not
          a total: once the ring is full each new fetch evicts an old one, so these rise and fall as
          history ages out. That is what makes p50/goodput reflect now instead of the whole session. */}
      <Section title={`window · last ${events.length} events`}>
        <Net label="tiles" n={t.tiles} />
        <Net label="facts" n={t.facts} />
        {/* Shown when a remote fold actually moved bytes, not when the manifest says it might. */}
        {t.foldBytes > 0 && <Line label="fold" v={bytes(t.foldBytes)} note={`${count(st, 'fold.remote')} req`} />}
        <Line label="goodput" v={t.throughputMBs ? `${t.throughputMBs.toFixed(1)} MB/s` : '—'} note="cold only" />
        <Line label="decoded" v={bytes(t.tiles.decoded + t.facts.decoded)} note="consumed" />
      </Section>

      {/* Both clocks, always. The engine prices the route per view at bake and may delegate to the server
          on any of them, so a panel that branched on the current view's route would have to be told which
          answer to expect — and would show nothing the moment it guessed wrong. These just run: whichever
          route executes populates its own rows, and switching views needs no flag and no reload. */}
      <Section title={`fold · ${routeLabel(manifest)}`}>
        <Clock label="client" s={stat(st, 'fold.client')} note="foldTile" />
        <Clock label="server" s={stat(st, 'fold.server')} note="DuckDB" />
        <Clock label="round trip" s={stat(st, 'fold.remote')} note="POST" />
        {/* Mean-vs-mean: the gap is transport + queueing. Taking it from p50s would subtract two
            different requests' numbers and produce a difference that belongs to neither. */}
        <Line
          label="transport"
          v={t.foldRoundTripMs !== null && t.foldServerMs !== null ? ms(t.foldRoundTripMs - t.foldServerMs) : '—'}
          note="trip − server"
        />
      </Section>

      <Section title="stages · ms">
        <table style={{ width: '100%', borderCollapse: 'collapse', fontVariantNumeric: 'tabular-nums' }}>
          <thead>
            <tr style={{ opacity: 0.5 }}>
              <th style={{ ...cell, textAlign: 'left' }}>stage</th>
              <th style={cell}>n</th>
              <th style={cell}>p50</th>
              <th style={cell}>p95</th>
              <th style={cell}>Σ</th>
            </tr>
          </thead>
          <tbody>
            {st.length === 0 && (
              <tr>
                <td colSpan={5} style={{ ...cell, textAlign: 'left', opacity: 0.5 }}>
                  move the map to collect
                </td>
              </tr>
            )}
            {st.map((s) => (
              <tr key={s.stage}>
                <td style={{ ...cell, textAlign: 'left' }}>{s.stage}</td>
                <td style={cell}>{s.n}</td>
                <td style={cell}>{s.p50.toFixed(1)}</td>
                <td style={{ ...cell, color: s.p95 >= LONG_FRAME_MS ? 'var(--warn)' : undefined }}>{s.p95.toFixed(1)}</td>
                <td style={{ ...cell, opacity: 0.6 }}>{s.total.toFixed(0)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <div style={{ ...sub, marginTop: 6 }}>
        {tilesInView} tiles in view · {c.events.toLocaleString()} events total · ↓fetched ⚡cached{' '}
        {t.tiles.unknown + t.facts.unknown > 0 ? '?unreadable' : ''}
      </div>
    </div>
  );
}

/** Last ~120 frames, tallest = slowest. A layer-admission hitch is the spike; the flat run is a steady
 *  pan. Bars clamp at 120ms so a 230ms hitch doesn't flatten every 16ms frame to nothing. */
function FrameGraph({ frames }: { frames: readonly number[] }) {
  const show = frames.slice(-120);
  return (
    <div style={graph}>
      {show.map((f, i) => {
        const h = Math.max(1, Math.min(100, (f / 120) * 100));
        return (
          <div
            key={i}
            style={{
              flex: 1,
              height: `${h}%`,
              alignSelf: 'flex-end',
              background: f >= LONG_FRAME_MS ? 'var(--warn)' : f >= 20 ? 'var(--card-fg)' : 'var(--good)',
              opacity: f >= LONG_FRAME_MS ? 1 : 0.55,
            }}
          />
        );
      })}
      {show.length === 0 && <span style={{ ...sub, alignSelf: 'center' }}>{emptyFramesNote()}</span>}
    </div>
  );
}

/** requestAnimationFrame is suspended while the tab isn't painting, so an empty graph there means
 *  "paused", not "fast". Saying so keeps a hidden tab from reading as a perfectly smooth one. */
const emptyFramesNote = (): string =>
  typeof document !== 'undefined' && document.hidden ? 'paused — tab hidden' : 'sampling…';

/** A network stage's honest shape: bytes that actually crossed the wire, and how many requests didn't
 *  have to. `unknown` only appears when the browser refused to say (no Timing-Allow-Origin). */
const Net = ({ label, n }: { label: string; n: { reqs: number; cold: number; warm: number; unknown: number; wire: number } }) => (
  <div style={row}>
    <span style={{ opacity: 0.75 }}>{label}</span>
    <span style={{ flex: 1 }} />
    <span style={{ fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>{bytes(n.wire)}</span>
    <span style={{ ...sub, width: 62, textAlign: 'right' }}>
      {n.cold}↓{n.warm ? ` ${n.warm}⚡` : ''}
      {n.unknown ? ` ${n.unknown}?` : ''}
    </span>
  </div>
);

const Section = ({ title, children }: { title: string; children: React.ReactNode }) => (
  <div style={{ marginTop: 8, borderTop: '1px solid var(--card-border)', paddingTop: 6 }}>
    <div style={{ ...sub, marginBottom: 3 }}>{title}</div>
    {children}
  </div>
);

const Line = ({ label, v, note }: { label: string; v: string; note?: string }) => (
  <div style={row}>
    <span style={{ opacity: 0.75 }}>{label}</span>
    <span style={{ flex: 1 }} />
    <span style={{ fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>{v}</span>
    {note && <span style={{ ...sub, width: 62, textAlign: 'right' }}>{note}</span>}
  </div>
);

/** One stage's clock: p50, with p95 and sample count alongside. Reads "—" until the stage has actually
 *  run — never 0.0, which would read as "instant" for a route that simply never executed. */
const Clock = ({ label, s, note }: { label: string; s?: StageStat; note: string }) => (
  <div style={row}>
    <span style={{ opacity: 0.75 }}>{label}</span>
    <span style={{ flex: 1 }} />
    <span style={{ fontWeight: 600, fontVariantNumeric: 'tabular-nums', opacity: s ? 1 : 0.4 }}>
      {s ? `${s.p50.toFixed(1)} ms` : '—'}
    </span>
    <span style={{ ...sub, width: 78, textAlign: 'right' }}>{s ? `p95 ${s.p95.toFixed(0)} · n${s.n}` : note}</span>
  </div>
);

/** What the bake priced for this view, and whether a URL flag is overriding it. Informational only: the
 *  clocks report what actually ran, which is exactly the thing that can disagree with this label. */
function routeLabel(m: Manifest | null): string {
  if (!m) return '—';
  const baked = m.foldRoute?.execution ?? 'client';
  const forced = foldRouteOverride();
  return forced && forced !== baked ? `${forced} · forced, baked ${baked}` : baked;
}

const stat = (st: ReturnType<typeof stats>, s: PerfStage) => st.find((x) => x.stage === s);
const count = (st: ReturnType<typeof stats>, s: PerfStage) => stat(st, s)?.n ?? 0;
const ms = (v: number | null) => (v === null ? '—' : `${v.toFixed(1)} ms`);
const bytes = (b: number): string =>
  b >= 1e6 ? `${(b / 1e6).toFixed(2)} MB` : b >= 1e3 ? `${(b / 1e3).toFixed(1)} KB` : `${b} B`;

const shell: React.CSSProperties = {
  position: 'absolute',
  zIndex: 3, // above the deck canvas and the MapLibre ctrl corners
  bottom: 12,
  right: 12,
  width: 280,
  padding: '8px 10px',
  background: 'var(--card-bg)',
  color: 'var(--card-fg)',
  font: '11px ui-monospace, SFMono-Regular, Menlo, monospace',
  borderRadius: 8,
  border: '1px solid var(--card-border)',
  boxShadow: 'var(--card-shadow)',
  userSelect: 'none',
};

const collapsed: React.CSSProperties = { width: 'auto', cursor: 'pointer', padding: '4px 10px' };
const head: React.CSSProperties = { display: 'flex', alignItems: 'center', gap: 6 };
const mini: React.CSSProperties = { ...btn, padding: '1px 5px', fontSize: 10, font: 'inherit' };
const row: React.CSSProperties = { display: 'flex', alignItems: 'baseline', gap: 6, lineHeight: 1.7 };
const sub: React.CSSProperties = { opacity: 0.5, fontSize: 10 };
const cell: React.CSSProperties = { padding: '1px 0', textAlign: 'right', fontWeight: 400 };
const graph: React.CSSProperties = {
  display: 'flex',
  alignItems: 'flex-end',
  gap: 1,
  height: 34,
  marginBottom: 3,
  background: 'var(--input-bg)',
  borderRadius: 3,
  overflow: 'hidden',
};
