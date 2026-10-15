Cormorant Garamond and Lora, both under the SIL Open Font License 1.1 (see `LICENSE` in each
folder). The Classical design system's two faces (issue #255): Cormorant Garamond for headings, Lora
for body text.

Vendored rather than loaded from Google Fonts, for the same reason as uPlot (issue #44): the UI has to
render during an internet outage. The `@font-face` rules that use them are at the top of
`css/site.css`.

The files are the unmodified variable-weight (`wght` axis) woff2 builds from Fontsource 5.3.0,
`@fontsource-variable/cormorant-garamond` and `@fontsource-variable/lora` on npm, in the `latin` and
`latin-ext` subsets only -- `latin-ext` carries Czech. Lora has its italic too; Cormorant Garamond's
is left out because nothing sets a heading in italic. To upgrade, replace the files with a newer
version's and bump the number above.
