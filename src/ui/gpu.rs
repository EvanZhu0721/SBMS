use slint::{ModelRc, VecModel};

use super::QuickAccess;

pub(super) fn selection(ui: &QuickAccess) -> Option<String> {
    let id = ui.get_preferred_gpu();
    (!id.is_empty()).then(|| id.to_string())
}

pub(super) fn refresh_options(ui: &QuickAccess) {
    let preferred = ui.get_preferred_gpu();
    let mut names = vec!["Automatic".into()];
    let mut ids = vec!["".into()];
    match crate::gpu_adapter::enumerate() {
        Ok(adapters) => {
            for gpu in adapters {
                names.push(gpu.name.into());
                ids.push(gpu.id.into());
            }
        }
        Err(error) => {
            crate::diagnostics::log(
                crate::diagnostics::Level::Warn,
                "ui",
                "gpu-list",
                None,
                &error,
            );
            ui.set_error_text(format!("Couldn’t list GPUs: {error}").into());
        }
    }
    let selected = ids
        .iter()
        .position(|id: &slint::SharedString| id.eq_ignore_ascii_case(&preferred))
        .unwrap_or_else(|| {
            names.push("Unavailable GPU".into());
            ids.push(preferred);
            ids.len() - 1
        });
    ui.set_gpu_names(ModelRc::new(VecModel::from(names)));
    ui.set_gpu_ids(ModelRc::new(VecModel::from(ids)));
    ui.set_selected_gpu(selected as i32);
}
