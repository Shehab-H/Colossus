import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { registerTileCache } from './lib/swClient'

// Persistent tile cache (production only): serves immutable versioned tiles from the Cache API so
// reloads and returning sessions render offline of the tile server.
registerTileCache()

// No <StrictMode>: its dev-only double-invoke double-mounts the react-map-gl `useControl` deck overlay,
// leaving a stale second GL canvas. deck.gl + react-map-gl apps conventionally run without it.
createRoot(document.getElementById('root')!).render(<App />)
