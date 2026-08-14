# Colossus web

React 19 application for the Colossus map control plane, full viewer, and embeddable viewer. The UI uses a
Vodafone-inspired Sass design system and product-oriented dependency boundaries documented in
[`ARCHITECTURE.md`](ARCHITECTURE.md).

## Development

```powershell
npm install
npm run dev
```

The Vite application runs on `http://127.0.0.1:5173` by default.

## Component workbench

```powershell
npm run storybook
```

Storybook runs on `http://127.0.0.1:6006`. It exposes the shared primitives and composed viewer widgets in
light and dark themes, with interaction and accessibility checks available from the testing panel.

## Quality gates

```powershell
npm run lint
npm test
npm run test:storybook
npm run test:visual
npm run build
npm run build:storybook
```

Use `npm run test:visual:update` only when an intentional visual change has been reviewed. A fresh machine
can install the visual-test browser with `npm run test:browser:install`.
