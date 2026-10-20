use windows::Win32::Devices::Display::*;

fn main() {
    for flags in [
        QDC_ONLY_ACTIVE_PATHS,
        QDC_ONLY_ACTIVE_PATHS | QDC_VIRTUAL_MODE_AWARE,
    ] {
        unsafe {
            let (mut pc, mut mc) = (0, 0);
            println!(
                "FLAGS {:?} sizes {:?}",
                flags,
                GetDisplayConfigBufferSizes(flags, &mut pc, &mut mc)
            );
            let mut paths = vec![DISPLAYCONFIG_PATH_INFO::default(); pc as usize];
            let mut modes = vec![DISPLAYCONFIG_MODE_INFO::default(); mc as usize];
            println!(
                "query {:?}",
                QueryDisplayConfig(
                    flags,
                    &mut pc,
                    paths.as_mut_ptr(),
                    &mut mc,
                    modes.as_mut_ptr(),
                    None
                )
            );
            for (i, p) in paths[..pc as usize].iter().enumerate() {
                println!(
                    "path {i}: source id={} raw_mode={:#x} target id={} raw_mode={:#x} rotation={:?} flags={:#x}",
                    p.sourceInfo.id,
                    p.sourceInfo.Anonymous.modeInfoIdx,
                    p.targetInfo.id,
                    p.targetInfo.Anonymous.modeInfoIdx,
                    p.targetInfo.rotation,
                    p.flags
                );
            }
            for (i, m) in modes[..mc as usize].iter().enumerate() {
                print!("mode {i}: id={} type={:?} ", m.id, m.infoType);
                if m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE {
                    let s = m.Anonymous.sourceMode;
                    println!(
                        "source {}x{} at {},{}",
                        s.width, s.height, s.position.x, s.position.y
                    );
                } else if m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE_TARGET {
                    let t = m.Anonymous.targetMode.targetVideoSignalInfo;
                    println!("target active={}x{}", t.activeSize.cx, t.activeSize.cy);
                } else if m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE_DESKTOP_IMAGE {
                    println!("desktop {:?}", m.Anonymous.desktopImageInfo);
                } else {
                    println!();
                }
            }
        }
    }
    println!("active_displays {:#?}", sbms::display::active_displays());
}
