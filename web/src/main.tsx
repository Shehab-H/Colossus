import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { registerTileCache } from './lib/swClient'
import { initResourceTiming, installPerfGlobal, perfEnabled, setPerfEnabled } from './lib/perf'

// ?perf=1 arms every lifecycle probe. Set before the first render — the tile loader reads this flag when
// it posts to the decode workers, so a probe armed later would miss the opening tile set.
setPerfEnabled(perfEnabled())
initResourceTiming()
installPerfGlobal()

// Persistent tile cache (production only): serves immutable versioned tiles from the Cache API so
// reloads and returning sessions render offline of the tile server.
registerTileCache()

// No <StrictMode>: its dev-only double-invoke double-mounts the react-map-gl `useControl` deck overlay,
// leaving a stale second GL canvas. deck.gl + react-map-gl apps conventionally run without it.
createRoot(document.getElementById('root')!).render(<App />)
