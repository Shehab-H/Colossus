import { cx } from '@/utils/classNames';
import { LONG_FRAME_MS } from './frameProbe';

/** Last ~120 frames, tallest = slowest. A layer-admission hitch is the spike; the flat run is a steady
 *  pan. Bars clamp at 120ms so a 230ms hitch doesn't flatten every 16ms frame to nothing. */
export function FrameGraph({ frames }: { frames: readonly number[] }) {
  const show = frames.slice(-120);
  return (
    <div className="perf-dashboard__graph">
      {show.map((f, i) => {
        const h = Math.max(1, Math.min(100, (f / 120) * 100));
        return (
          <div
            key={i}
            className={cx(
              'perf-dashboard__bar',
              f >= LONG_FRAME_MS ? 'is-long' : f >= 20 ? 'is-slow' : 'is-smooth',
            )}
            style={{ height: `${h}%` }}
          />
        );
      })}
      {show.length === 0 && <span className="perf-dashboard__graph-empty">{emptyFramesNote()}</span>}
    </div>
  );
}

/** requestAnimationFrame is suspended while the tab isn't painting, so an empty graph there means
 *  "paused", not "fast". Saying so keeps a hidden tab from reading as a perfectly smooth one. */
const emptyFramesNote = (): string =>
  typeof document !== 'undefined' && document.hidden ? 'paused — tab hidden' : 'sampling…';
