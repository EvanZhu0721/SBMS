fn main() {
    println!("cargo:rerun-if-changed=assets/app.manifest");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows")
        && std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc")
    {
        let manifest = std::path::PathBuf::from(
            std::env::var_os("CARGO_MANIFEST_DIR").expect("Cargo provides the package directory"),
        )
        .join("assets/app.manifest");
        println!("cargo:rustc-link-arg=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg=/MANIFESTINPUT:{}", manifest.display());
    }
    // Distinguish diagnostic builds that share the public package version.
    println!("cargo:rerun-if-changed=src");
    println!("cargo:rerun-if-changed=Cargo.toml");
    let stamp = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .expect("build clock predates Unix epoch")
        .as_secs();
    println!("cargo:rustc-env=SBMS_BUILD_STAMP={stamp}");
    let config = slint_build::CompilerConfiguration::new()
        .with_style("material".into())
        .with_include_paths(vec!["ui/vendor/material".into()]);
    slint_build::compile_with_config("ui/quick-access.slint", config)
        .expect("failed to compile the SBMS tray UI");
}
