use sbms::ui::QuickAccess;
use slint::{ComponentHandle, ModelRc, SharedString, VecModel};
fn model(values: &[&str]) -> ModelRc<SharedString> {
    ModelRc::new(VecModel::from(
        values
            .iter()
            .map(|s| SharedString::from(*s))
            .collect::<Vec<_>>(),
    ))
}
fn main() -> Result<(), Box<dyn std::error::Error>> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .select()?;
    let ui = QuickAccess::new()?;
    ui.set_display_names(model(&["P245MS"]));
    ui.set_display_labels(model(&["P245MS"]));
    ui.set_selected_display(0);
    ui.set_reference_names(model(&["G27K-180"]));
    ui.set_reference_labels(model(&["G27K-180"]));
    ui.set_selected_reference(0);
    ui.set_tab_labels(model(&["P245MS"]));
    ui.set_tab_details(model(&["1920 × 1080"]));
    ui.set_tab_indices(ModelRc::new(VecModel::from(vec![0])));
    let args: Vec<_> = std::env::args().collect();
    ui.set_profile_saving(args.iter().any(|s| s == "--saving"));
    if args.iter().any(|s| s == "--long") {
        ui.set_active_profile_name("Long profile name for review".into());
    }
    ui.show()?;
    ui.window()
        .set_position(slint::PhysicalPosition::new(100, 100));
    ui.run()?;
    Ok(())
}
