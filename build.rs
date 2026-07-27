fn main() {
    let config = slint_build::CompilerConfiguration::new().with_style("material".into());
    slint_build::compile_with_config("ui/quick-access.slint", config)
        .expect("failed to compile the SBMS tray UI");
}
