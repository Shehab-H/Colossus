import { Profiler } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { registerTileCache } from './lib/swClient'
import { initResourceTiming, installPerfGlobal, perfEnabled, record, setPerfEnabled } from './lib/perf'

// ?perf=1 arms every lifecycle probe. Set before the first render — the tile loader reads this flag when
// it posts to the decode workers, so a probe armed later would miss the opening tile set.
const perf = perfEnabled()
setPerfEnabled(perf)
initResourceTiming()
installPerfGlobal()

// Persistent tile cache (production only): serves immutable versioned tiles from the Cache API so
// reloads and returning sessions render offline of the tile server.
registerTileCache()

// No <StrictMode>: its dev-only double-invoke double-mounts the react-map-gl `useControl` deck overlay,
// leaving a stale second GL canvas. deck.gl + react-map-gl apps conventionally run without it.
const app = <App />
createRoot(document.getElementById('root')!).render(
  perf ? (
    <Profiler
      id="app"
      onRender={(_id, _phase, actualDuration) =>
        record({ stage: 'react.commit', ms: actualDuration, t: performance.now() })
      }
    >
      {app}
    </Profiler>
  ) : app,
)
