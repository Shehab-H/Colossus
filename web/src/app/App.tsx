import MapViewerPage from '@features/map-viewer/MapViewerPage';

/** Application composition root. Management and viewer routes will be selected here; the viewer feature
 * stays independently testable and knows nothing about future product-shell state. */
export default function App() {
  return <MapViewerPage />;
}
