# Third-Party Notices

This file lists the third-party projects, packages and assets that q38-vs
relates to, depends on, or derives from, along with their licenses.

## 1. Upstream project: qwenthin/q38

- Project: [qwenthin/q38](https://github.com/wu1w/qwenthin)
- License: Apache License 2.0 (Apache-2.0)

Notes:

- The upstream repository currently lacks a LICENSE file containing the full
  license text; the Apache-2.0 attribution above reflects the upstream
  project's stated licensing.
- q38-vs does **not** include or redistribute any upstream source code. It
  only interoperates with the q38 console through its process interface and
  HTTP API.
- The q38 console version verified against this plugin is commit `812610f`.
  Upstream `main` has since advanced to commit `60343d9`, which has **not**
  been verified with q38-vs.

## 2. NuGet dependencies

| Package | License |
| --- | --- |
| Microsoft.VisualStudio.SDK | Microsoft Software License |
| Microsoft.VSSDK.BuildTools | Microsoft Software License |
| Microsoft.Web.WebView2 | MIT |
| System.Text.Json | MIT |

## 3. Icons and design tokens

The icons and design tokens referenced by q38-vs (the `q38-ink`/`paper`
color palette) originate from the official q38 console UI. They are used as
visual reference only; no console code was copied.

## License

q38-vs itself is licensed under the MIT License — see [LICENSE](LICENSE).
