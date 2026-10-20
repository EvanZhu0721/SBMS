use sbms::display::{active_displays, restore_display_topology};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let original: Vec<_> = active_displays()?
        .into_iter()
        .filter(|display| !display.virtual_display)
        .collect();
    let target = original
        .iter()
        .position(|display| !display.primary)
        .ok_or("a secondary physical display is required")?;
    let mut shifted = original.clone();
    shifted[target].rect.top += 8;
    shifted[target].rect.bottom += 8;
    let exercise = restore_display_topology(&shifted).and_then(|()| {
        let current = active_displays()?;
        Ok(current)
    });
    let restored = restore_display_topology(&original);
    let current = exercise?;
    restored?;
    let actual = current.iter().find(|d| d.id == shifted[target].id).unwrap();
    println!(
        "shifted_rect={:?} rotation={:?}",
        actual.rect, actual.rotation
    );
    assert_eq!(actual.rect, shifted[target].rect);
    assert_eq!(actual.rotation, shifted[target].rotation);
    let final_displays = active_displays()?;
    for saved in &original {
        let actual = final_displays.iter().find(|d| d.id == saved.id).unwrap();
        assert_eq!(actual.rect, saved.rect);
        assert_eq!(actual.rotation, saved.rotation);
        assert_eq!(actual.refresh_numerator, saved.refresh_numerator);
        assert_eq!(actual.refresh_denominator, saved.refresh_denominator);
    }
    println!("physical topology restored exactly");
    let old_primary = original.iter().position(|d| d.primary).unwrap();
    let mut exchanged = original.clone();
    let dx = exchanged[target].rect.left;
    let dy = exchanged[target].rect.top;
    for (index, display) in exchanged.iter_mut().enumerate() {
        display.rect.left -= dx;
        display.rect.right -= dx;
        display.rect.top -= dy;
        display.rect.bottom -= dy;
        display.primary = index == target;
    }
    // Reproduce the reported relative layout on a two-monitor desktop:
    // previous primary is above the new primary, offset left by 447 pixels.
    if exchanged.len() == 2 {
        let display = &mut exchanged[old_primary];
        let width = display.rect.right - display.rect.left;
        let height = display.rect.bottom - display.rect.top;
        display.rect.left = -447;
        display.rect.right = width - 447;
        display.rect.top = -height;
        display.rect.bottom = 0;
    }
    let exercise = restore_display_topology(&exchanged).and_then(|()| active_displays());
    let restored = restore_display_topology(&original);
    let current = exercise?;
    restored?;
    for saved in &exchanged {
        let actual = current.iter().find(|d| d.id == saved.id).unwrap();
        assert_eq!(actual.rect, saved.rect);
        assert_eq!(actual.primary, saved.primary);
        assert_eq!(actual.rotation, saved.rotation);
        assert_eq!(actual.refresh_numerator, saved.refresh_numerator);
        assert_eq!(actual.refresh_denominator, saved.refresh_denominator);
    }
    let current = active_displays()?;
    for saved in &original {
        let actual = current.iter().find(|d| d.id == saved.id).unwrap();
        assert_eq!(actual.rect, saved.rect);
        assert_eq!(actual.primary, saved.primary);
        assert_eq!(actual.rotation, saved.rotation);
        assert_eq!(actual.refresh_numerator, saved.refresh_numerator);
        assert_eq!(actual.refresh_denominator, saved.refresh_denominator);
    }
    println!("primary exchange and offset layout restored exactly");
    Ok(())
}
