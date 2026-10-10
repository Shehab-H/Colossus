import { useEffect, useState, useSyncExternalStore } from 'react';
import type { Manifest } from '@/types/manifest';
import { reloadViewerCold } from './viewerDiagnostics';
import { Button, Card } from '@/components/ui';
import type { CacheGauge } from './cacheGauge';
import { clearPerf, cumulative, deckSnapshot, frameSamples, perfEvents, perfStore } from '@/lib/perf/recorder';
import { gpuName } from './gpuName';
import { LONG_FRAME_MS, startFrameProbe } from './frameProbe';
import { stats } from './stats';
import { totals } from './totals';
import { Section } from './Section';
import { FrameGraph } from './FrameGraph';
import { Line } from './Line';
import { bytes, count, stat, ms } from './dashboardFormat';
import { Net } from './Net';
import { routeLabel } from './routeLabel';
import { Clock } from './Clock';

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
  const [open, setOpen] = useState(false);

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
      <Button className="perf-dashboard__collapsed" variant="secondary" size="sm" onClick={() => setOpen(true)}>
        ▲ perf
      </Button>
    );

  return (
    <Card className="perf-dashboard">
      <div className="perf-dashboard__header">
        <strong>perf</strong>
        <span
          className="perf-dashboard__context"
          title={manifest ? `${manifest.view.id} · ${manifest.version}` : undefined}
        >
          {manifest ? `${manifest.view.id} · ${manifest.version}` : '—'}
        </span>
        <Button className="perf-dashboard__action" variant="ghost" size="sm" onClick={() => clearPerf()} title="Reset the window — pan somewhere, clear, then measure the thing you care about">
          clear
        </Button>
        {/* The disk cache is cache-first, so once warm it answers every tile forever and a cold load
            becomes unmeasurable. This drops the SW and its caches, then reloads uncontrolled. */}
        <Button
          className="perf-dashboard__action"
          variant="ghost"
          size="sm"
          title="Drop the service worker + tile disk caches and reload — measures a genuinely cold load"
          onClick={() => {
            void reloadViewerCold();
          }}
        >
          cold
        </Button>
        <Button className="perf-dashboard__action" variant="ghost" size="sm" iconOnly onClick={() => setOpen(false)} aria-label="Collapse performance dashboard">
          ▼
        </Button>
      </div>

      <Section title="frames">
        <FrameGraph frames={frames} />
        <div className="perf-dashboard__row">
          <span>{t.fps ? `${t.fps.toFixed(0)} fps` : '—'}</span>
          <span className={t.longFrames ? 'perf-dashboard__warning' : 'perf-dashboard__success'}>
            {t.longFrames} long (≥{LONG_FRAME_MS}ms)
          </span>
          <span className="perf-dashboard__muted">max {frames.length ? Math.max(...frames).toFixed(0) : 0}ms</span>
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
          <div className="perf-dashboard__subtle">waiting for deck (publishes once a second)</div>
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
        <table className="perf-dashboard__table">
          <thead>
            <tr>
              <th>stage</th>
              <th>n</th>
              <th>p50</th>
              <th>p95</th>
              <th>Σ</th>
            </tr>
          </thead>
          <tbody>
            {st.length === 0 && (
              <tr>
                <td colSpan={5} className="perf-dashboard__empty-cell">
                  move the map to collect
                </td>
              </tr>
            )}
            {st.map((s) => (
              <tr key={s.stage}>
                <td>{s.stage}</td>
                <td>{s.n}</td>
                <td>{s.p50.toFixed(1)}</td>
                <td className={s.p95 >= LONG_FRAME_MS ? 'perf-dashboard__warning' : undefined}>{s.p95.toFixed(1)}</td>
                <td className="perf-dashboard__muted">{s.total.toFixed(0)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <div className="perf-dashboard__footnote">
        {tilesInView} tiles in view · {c.events.toLocaleString()} events total · ↓fetched ⚡cached{' '}
        {t.tiles.unknown + t.facts.unknown > 0 ? '?unreadable' : ''}
      </div>
    </Card>
  );
}
