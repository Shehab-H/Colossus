# Categorical color editor does not provide a usable category-to-color workflow

**Status:** Candidate  
**Last verified:** 2026-08-21  
**Estimate:** Medium (2–4 engineering days, including migration and UI tests)

## Summary

The map configuration wizard cannot reliably explain or edit categorical color mappings. It can render a
preview that contains real categories, generated placeholder categories, and an `other` entry at the same
time, while hiding the exact saved palette that produced the real colors. An operator must currently edit
advanced JSON or use an API to assign deterministic colors to known values.

The defect applies to every categorical source channel and every categorical computed measure, regardless
of what its values mean. Operator names are one reproduction case; status values, regions, product tiers,
feature classes, risk bands, and other categorical domains require the same editing model.

`mobile-coverage` is retained below only as a concrete regression example. After configuring four
categorical dominance measures over the same dimension, its saved palette contains `apex`, `nimbus`,
`orbit`, and `pulse`, but the wizard does not present those mappings as editable rows.

## Scope principle

The solution must be domain-driven, not map-specific:

- derive category values and result types from generic channel, measure-AST, manifest-domain, and authored
  color metadata;
- render the same editor for any categorical domain without special-casing channel names, measure names,
  map slugs, or particular values;
- treat the `mobile-coverage` operators as a reusable regression fixture, not as the product model;
- keep source dimensions, derived categorical measures, exact palettes, scheme-assigned colors, and
  partial overrides interoperable through one generic contract.

## Observed behavior

1. A measure such as `argmax(category, aggregate)` is labelled **computed measure · numeric** even though
   its result is categorical. In the reproduction, this is
   `dominant_operator_by_tests = argmax(operator, sum(tests))`.
2. The style preview can show real domain values and then fabricate `Category 1`, `Category 2`,
   `Category 3`, and `Category 4`. In the reproduction, the real values are `apex`, `nimbus`, `orbit`,
   and `pulse`.
3. The preview also shows `other` as if it were a real category. It is actually the unknown/out-of-domain
   fallback used by a closed exact palette.
4. **Category order** is empty even when an exact palette already supplies named categories.
5. **Category overrides** only renders entries from `overrides`; entries in the saved exact `palette` are
   not editable. The UI displays a legacy warning but offers no direct, lossless editing path.
6. **Add override** creates another placeholder such as `Category 1` instead of offering known values from
   the selected channel's domain.
7. **Customize colors** edits positional palette colors, not an explicit `category → color` mapping, so it
   is unclear which domain value will receive each color.
8. The published viewer can keep a shared semantic palette across compatible categorical measures, but the
   management wizard does not explain or configure that relationship.

## Root causes and integration surface

- [MapConfigurationBuilder.tsx](../../web/src/features/map-management/ui/MapConfigurationBuilder.tsx)
  treats every computed measure as numeric through `measureColorOption`, instead of deriving the result
  type from the measure expression.
- [ColorEncodingEditor.tsx](../../web/src/features/map-management/ui/ColorEncodingEditor.tsx) derives the
  editable category list only from `domain`, renders only `overrides` as editable rows, and adds four
  illustrative `Category N` values unconditionally to categorical previews.
- [colorScale.ts](../../web/src/domain/map/colorScale.ts) correctly treats an exact `palette` as closed and
  appends the unknown fallback to the legend as `other`; the editor fails to distinguish that fallback
  from real domain values.
- [colorEncoding.ts](../../web/src/domain/map/colorEncoding.ts) owns authoring defaults and shared-category
  palette retargeting, but the wizard does not yet expose a domain-aware named-category model.
- [useViewData.ts](../../web/src/features/map-viewer/hooks/useViewData.ts) consumes baked channel domains and
  demonstrates that categorical measures sharing the same domain can safely reuse one semantic palette.
- The published manifest's `channelDomains` is the authoritative category source after a successful bake.
  Before the first bake, `argmax`/`argmin` result domains can be derived from their dimension operand.

## Required experience

### Correctly classify the selected channel

- Parse computed measure expressions in the management UI using the same grammar as the viewer/bake.
- Label `argmax(dimension, aggregate)` and `argmin(dimension, aggregate)` as categorical measures.
- Label ordinary aggregates such as `sum`, `avg`, and `wavg` as numeric measures.
- Do not offer incompatible scale types as the recommended default.

### Replace placeholder-driven editing with named category rows

For any categorical channel, show a table with one row per known value. The labels below are illustrative,
not a fixed operator-specific schema:

| Category | Effective color | Mapping source | Action |
| --- | --- | --- | --- |
| known value A | color picker + hex | fixed or override | Reset/remove override |
| known value B | color picker + hex | fixed or inherited | Reset/remove override |
| known value C | color picker + hex | fixed or inherited | Reset/remove override |

Known categories should come from, in priority order:

1. the selected channel's non-truncated baked `channelDomains` values;
2. the dimension domain referenced by an `argmax`/`argmin` measure;
3. the union of authored `domain`, `palette` keys, and `overrides` keys;
4. placeholders only when no real category information exists, clearly labelled as preview-only.

Changing a row's color must create or update the appropriate named mapping without requiring JSON edits.
The UI must preserve exact palettes losslessly; it may offer an explicit conversion to scheme + partial
overrides, but must not require conversion merely to edit one color.

### Separate fallback semantics from real categories

- Present the unknown/null color in a separate **Fallback** section.
- Explain that it applies to null values and values outside a closed palette/domain.
- Do not label it `other` in the editor preview as if it were a dataset value. Use wording such as
  **Unknown / unmapped fallback**.
- Do not show the fallback swatch among known categories unless the configuration can actually produce an
  unmapped value; when shown, visually separate it.

### Make category order meaningful

- Populate category order from the effective known category list.
- Support reordering named values directly rather than asking operators to retype the full list.
- Explain that ordering controls automatic palette assignment and legend order, not filtering or data
  precedence.
- Never let a partial category-order edit silently turn all omitted known values into the unknown color.

### Support one semantic palette across compatible categorical choices

- Any channels or measures with the same semantic category domain may share one named-value palette.
- The wizard should show when compatible choices reuse a palette and provide one obvious place to edit it.
- A named value's color must remain effective when the viewer switches among compatible categorical
  choices.
- Numeric channels and categorical channels with a different domain must not inherit that palette.
- As a regression example, `dominant_operator_by_tests`, `dominant_operator_by_download`,
  `dominant_operator_by_upload`, and `dominant_operator_by_latency` share the same operator domain and
  should therefore keep the same named operator colors.

## Recommended implementation slices

1. Add measure-result type inference to the management-domain model and stop labelling all measures numeric.
2. Introduce a pure `effectiveCategories` helper that combines baked domain data with authored mappings and
   returns real values separately from the fallback.
3. Replace the current category override block with domain-aware named rows supporting exact palettes,
   inherited scheme colors, and partial overrides.
4. Remove unconditional `Category 1–4` injection when real categories are available.
5. Rename and visually separate the unknown fallback in editor previews and legends.
6. Add published-manifest domain loading to the map editor, with an AST-derived fallback for unbaked
   `argmax`/`argmin` measures.
7. Add non-destructive normalization/migration tests for legacy exact palettes.

## Risks and edge cases

- A draft may have no successful release, or its current measures may differ from the published manifest.
- High-cardinality or truncated categorical domains cannot be rendered as thousands of editable rows; the
  editor needs search, a cap, and an explicit partial-mapping mode.
- Palette keys may refer to values no longer in the data. Preserve them but mark them stale instead of
  silently deleting them.
- Category values may differ only by case or whitespace; preserve exact keys and surface collisions.
- A measure expression can be temporarily invalid while being edited. Show an unresolved type without
  destroying the saved color configuration.
- Switching between exact palettes and scheme + overrides must be reversible until save and must never
  remap categories silently.

## Acceptance criteria

- Any valid `argmax`/`argmin` measure is labelled categorical without checking its name or map.
- Any low-cardinality categorical domain displays editable rows for its actual known values.
- No `Category N` placeholders appear when a real category domain is available.
- Unknown/null is visually and semantically separate from real categories and is not presented as a real
  `other` value.
- Editing any named value survives save, reload, bake, and switching among compatible categorical choices.
- Categorical filters continue to work after palette edits.
- Existing exact palettes load and save without data loss; conversion to partial overrides is explicit.
- Numeric color channels retain their current continuous, diverging, and binned editing behavior.
- Unit tests cover result-type inference, effective-category assembly, fallback separation, and lossless
  palette conversion using neutral fixtures rather than product-specific names.
- A browser-level test covers editing one known category, saving, reloading, and verifying the published
  legend and map color.
- A `mobile-coverage` regression test confirms that `apex`, `nimbus`, `orbit`, and `pulse` are editable and
  retain their colors across its four dominance KPIs; this test must exercise the generic implementation.

## Non-goals

- Redesigning numeric color scales.
- Supporting arbitrary expression languages beyond the existing closed measure grammar.
- Fetching an unbounded full distinct-value list for high-cardinality dimensions.
- Adding operator-specific controls or hard-coded knowledge of the `mobile-coverage` map.
- Automatically choosing the product meaning of “best”; latency remains an explicit `argmin`, while
  throughput KPIs remain explicit `argmax` measures.
