# Provider / Model Icons

- Source: [lobehub/lobe-icons](https://github.com/lobehub/lobe-icons), npm package `@lobehub/icons-static-svg@1.95.1`
  (tarball https://registry.npmjs.org/@lobehub/icons-static-svg/-/icons-static-svg-1.95.1.tgz, downloaded 2026-10-16).
- License: MIT, Copyright (c) 2023 LobeHub. See `LICENSE`.
- Trademarks: the logos depicted are trademarks of their respective owners. The MIT license covers
  the SVG artwork/code only and grants no trademark rights; use logos only to identify the
  corresponding provider/model, without implying endorsement.

## Contents

`svg/` holds a subset of the package, unmodified:

- `<name>.svg` — mono variant, `fill="currentColor"` on the root `<svg>`, viewBox `0 0 24 24`.
- `<name>-color.svg` — brand-color variant (some use linear/radial gradients), viewBox `0 0 24 24`.
  Missing for brands that are monochrome by design (anthropic, openai, cursor, grok, xai, ollama, ...).

`png/` holds brand icons that LobeHub does not cover and that the vendor only publishes as bitmaps
(rendered by `ProviderLogo` through `ProviderRasterIcons`, not by `gen_provider_icons.py`):

- `nextcowork.png` — https://nextco.work/icon.png (1024×1024), cropped to the rounded-square bounds and
  resized to 128×128 (`sips`). Brand artwork of NextCoWork, used only to identify the provider.

Upstream also ships `-text`, `-text-cn`, `-brand`, `-brand-color` variants (not copied).
Model-name -> icon keyword rules: upstream `src/features/modelConfig.ts`.
