pub const MAX_OUTPUTS: usize = 16;
pub const MAX_VIRTUAL_DIMENSION: u32 = 16_384;
pub const MIN_REFRESH_HZ: u64 = 1;
pub const MAX_REFRESH_HZ: u64 = 1_000;
pub const MIN_REFRESH_MILLIHZ: u32 = (MIN_REFRESH_HZ * 1_000) as u32;
pub const MAX_REFRESH_MILLIHZ: u32 = (MAX_REFRESH_HZ * 1_000) as u32;
pub const MIN_PHYSICAL_MILLIMETERS: f64 = 10.0;
pub const MAX_PHYSICAL_MILLIMETERS: f64 = 10_000.0;
pub const MILLIMETERS_PER_INCH: f64 = 25.4;

pub fn valid_physical_millimeters(value: f64) -> bool {
    value.is_finite() && (MIN_PHYSICAL_MILLIMETERS..=MAX_PHYSICAL_MILLIMETERS).contains(&value)
}

pub fn valid_refresh_millihz(value: u32) -> bool {
    (MIN_REFRESH_MILLIHZ..=MAX_REFRESH_MILLIHZ).contains(&value)
}
