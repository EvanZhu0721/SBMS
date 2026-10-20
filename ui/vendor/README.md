# SBMS Material widgets

Source: [Slint 1.17.1 widgets](https://github.com/slint-ui/slint/tree/v1.17.1/internal/compiler/widgets), from the `i-slint-compiler` 1.17.1 crate. The subset is used under **GPL-3.0-only**, one of the upstream licensing alternatives. Original copyright and SPDX notices are retained. The license text is in [LICENSE](../../LICENSE).

SBMS fixes the Material palette instead of recoloring controls from the Windows accent color. Light/dark selection still follows the platform. `styling.slint` makes `accentify` return the upstream default color and reads the built-in color-scheme selector; the dark fallback uses that selector too.

Filled primary buttons use `#4F378B` with `#EADDFF` text in both schemes, sampled from the user's selected-tab reference. These fill/text roles are separate from accent colors used by focus outlines and text controls.

`std-widgets.slint` exports only the widgets used by SBMS. `std-widgets-impl.slint` supplies the compiler's context-menu implementation. Application imports explicitly target this subset, and the compiler include directory resolves implicit StyleMetrics and menu imports to the same palette. Existing UI review examples use the same imports and include directory.

Slint restricts its interface syntax and internal menu primitives to built-in files. `material/lineedit.slint` and `common/lineedit-base.slint` therefore expand the upstream LineEdit interface into explicit properties, callback aliases and forwarding functions. Other behavior is retained. Pure menu/spinner bases, internal context menus, the system color-scheme selector and icon SVGs are reused through `builtin:/` paths, rather than copied. Built-in common menus resolve `style-base.slint` and `menu.slint` through the project's include directory.

This subset is tied to Slint 1.17.1. When updating Slint, compare these sources against upstream and rerun the real-page accent-color and light/dark rendering checks, including text fields and context menus.

Run `cargo run --release --locked --example theme-color-review -- work/theme-color-review/after`
to render the real main and rendering pages in both schemes. It injects blue and
orange platform accent refreshes into an isolated, unshown window, checks the
primary-button fill against the reference color, and asserts that the page pixels do not change. Scheme changes also go through the platform context to verify automatic light/dark selection.
The context setter is a private Slint API used only by this regression fixture;
the application does not override platform settings at runtime.

## Files

- [common/lineedit-base.slint](common/lineedit-base.slint)
- [material/button.slint](material/button.slint)
- [material/components.slint](material/components.slint)
- [material/lineedit.slint](material/lineedit.slint)
- [material/menu.slint](material/menu.slint)
- [material/progressindicator.slint](material/progressindicator.slint)
- [material/scrollview.slint](material/scrollview.slint)
- [material/spinner.slint](material/spinner.slint)
- [material/std-widgets-impl.slint](material/std-widgets-impl.slint)
- [material/std-widgets.slint](material/std-widgets.slint)
- [material/style-base.slint](material/style-base.slint)
- [material/styling.slint](material/styling.slint)
- [material/switch.slint](material/switch.slint)
