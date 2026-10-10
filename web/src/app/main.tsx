import { createRoot } from 'react-dom/client'
import { QueryClientProvider } from '@tanstack/react-query'
import '@fontsource/inter/latin-400.css'
import '@fontsource/inter/latin-500.css'
import '@fontsource/inter/latin-600.css'
import '@fontsource/inter/latin-700.css'
import '@/styles/index.scss'
import '@/styles/global.scss'
import '@/components/layouts/ManagementLayout.scss'
import '@/features/maps/components/MapCatalogTable.scss'
import '@/app/routes/maps/MapCatalogPage.scss'
import '@/app/routes/maps/sourceDisclosure.scss'
import '@/app/routes/maps/MapEditorPage.scss'
import '@/features/maps/components/MapConfigurationBuilder.scss'
import '@/features/maps/components/ColorEncodingEditor.scss'
import '@/features/maps/components/BakeRunHistory.scss'
import '@/app/routes/maps/MapDetailPage.scss'
import App from './App'
import { registerTileCache, installPerfGlobal } from '@/features/map-viewer'
import { queryClient } from '@/api/queryClient'
import { initResourceTiming } from '@/lib/perf/resourceTiming'
import { perfEnabled, setPerfEnabled } from '@/lib/perf/recorder'

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
createRoot(document.getElementById('root')!).render(
  <QueryClientProvider client={queryClient}>
    <App />
  </QueryClientProvider>,
)
