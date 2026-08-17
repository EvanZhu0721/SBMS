const THEME_SOURCE: &str = include_str!("../ui/theme.slint");

#[test]
fn platform_palette_only_selects_the_color_scheme() {
    let palette_references = THEME_SOURCE
        .lines()
        .filter(|line| line.contains("Palette."))
        .collect::<Vec<_>>();

    assert_eq!(
        palette_references,
        [
            "    private property <bool> dark-color-scheme: Palette.color-scheme == ColorScheme.dark;"
        ]
    );
}

#[test]
fn semantic_brushes_use_fixed_light_and_dark_pairs() {
    let expected_pairs = [
        ("surface", "#2a282d", "#f8f3f9"),
        ("on-surface", "#e6e1e5", "#1c1b1f"),
        ("surface-container", "#1c1b1f", "#fffbfe"),
        ("on-surface-variant", "#e6e1e5", "#1c1b1f"),
        ("primary", "#cacaca", "#343236"),
        ("on-primary", "#343236", "#ffffff"),
        ("primary-container", "#4f378b", "#eaddff"),
        ("on-primary-container", "#eaddff", "#21005d"),
        ("error", "#ffb4ab", "#ba1a1a"),
        ("error-container", "#93000a", "#ffdad6"),
        ("on-error-container", "#ffdad6", "#410002"),
        ("success-container", "#173824", "#d5f5df"),
        ("on-success-container", "#8fdaa5", "#155c2d"),
        ("outline-variant", "#938f99", "#79747e"),
    ];

    for (role, dark, light) in expected_pairs {
        let declaration =
            format!("out property <brush> {role}: dark-color-scheme ? {dark} : {light};");
        assert!(
            THEME_SOURCE.contains(&declaration),
            "missing stable theme declaration: {declaration}"
        );
    }

    assert_eq!(THEME_SOURCE.matches("out property <brush>").count(), 14);
}
