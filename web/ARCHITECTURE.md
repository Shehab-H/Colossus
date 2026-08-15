# Frontend architecture

Colossus uses product-oriented frontend layers and a shared design system. Dependency direction, UI
composition, and runtime integration are separate concerns; a folder name is not considered an
architecture boundary unless the import test enforces it.

## Engineering criteria

Choose dependencies and patterns solely for fitness to the problem, maintainability, accessibility,
performance, security, ecosystem quality, and operational cost. Use a capable maintained library whenever it
produces a stronger system than bespoke code.

## Layers

Dependencies point down this list:

1. `app` — composition root and route selection.
2. `pages` — route-level screens and orchestration.
3. `widgets` — substantial reusable page regions such as the map catalog, management shell, and viewer shell.
4. `features` — user capabilities and their application state.
5. `entities` — product concepts, typed API contracts, and entity-owned data access.
6. `shared` — design system, theme, generic API configuration, utilities, and observability.

The renderer's existing `domain` and `infrastructure` folders remain supported migration boundaries. New
management work should use the layers above; renderer code moves only when a change provides concrete value
and keeps dense-map behavior verified.

[`src/architecture.test.ts`](src/architecture.test.ts) is the executable dependency rule.

## Routing and server state

React Router's data router owns route matching, parameters, legacy redirects, internal navigation, and route
loading. Route objects live in `src/app`; pages receive only the parameters they need and do not inspect the
browser pathname directly.

TanStack Query owns remote catalog state:

- entity API modules expose typed query-option factories and hierarchical cache keys;
- route loaders prefetch those same options with `ensureQueryData` so navigation does not create request
  waterfalls or a second cache;
- feature API modules own mutations and the invalidation boundaries for the capabilities they implement;
- pages own transient form, dialog, and presentation state only.

Viewer camera and filter state remains URL-addressable feature state. It is updated through router navigation
so browser history and React rendering agree on one location source of truth.

## Bundle boundaries

Management and viewer route modules are loaded independently. Viewer-only Sass and the renderer stack stay
behind the viewer route; opening the control plane does not download MapLibre, deck.gl, Arrow, or Zstandard.

Vite's Rolldown groups split renderer and columnar-data dependencies inside that lazy boundary. MapLibre
remains one isolated lazy module because splitting its internals would add execution-order risk without
improving control-plane startup. `scripts/verify-bundle.mjs`, run by `npm run build`, reads the Vite manifest
and fails if the viewer stops being dynamic or the initial JavaScript graph exceeds 450 KiB.

## Design system

The shared UI contract lives under `src/shared/ui`:

- Sass maps define spacing, typography, radii, elevation, motion, and semantic color scales.
- Sass emits CSS custom properties because light/dark theme changes happen at runtime.
- React primitives own accessible markup and variants for buttons, links, cards, fields, feedback, badges,
  dialogs, page headers, and status summaries.
- Widgets and pages compose primitives; they do not create parallel button, field, or card systems.

The visual direction is Vodafone-inspired rather than a reproduction of Vodafone-owned assets: decisive red,
high black/white contrast, optimistic whitespace, rounded controls, and a circular Colossus brand cue.

## Component workbench

`.storybook` is the executable catalog for the shared UI contract. Stories compose real primitives and
widgets; they do not define a second production component library.

- The theme toolbar writes the same `data-theme` attribute used by the application.
- Storybook's Vitest integration renders every story in Chromium and treats axe accessibility findings as
  test failures in both themes.
- Playwright captures reviewed screenshots for the design-system showcase and composed viewer workspace in
  light and dark themes.
- Interaction tests belong in story `play` functions when the behavior is intrinsic to the component state.
- Baseline changes require review; do not update screenshots simply to make a failure disappear.

The workbench is intentionally outside `src` because it is development infrastructure. Its story modules may
import any product layer to compose representative states, while production dependency direction remains
enforced by `src/architecture.test.ts`.

## Styling ownership

- `shared/ui/styles` owns tokens, mixins, and primitive styles.
- Each widget owns its layout stylesheet beside the widget.
- Each page group owns only page composition styles.
- `app/styles.scss` owns only the document reset and unknown-route fallback.
- `widgets/map-viewer/viewer-shell.scss` owns viewer, embed, control, legend, inspection, and diagnostics styling.
- Hard-coded colors are allowed only for data visualization semantics or third-party canvas integration; product
  interface color must use semantic tokens.
