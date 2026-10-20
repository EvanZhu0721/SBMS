//! Dirty-source propagation through the unchanged sampling kernels.
use windows::Win32::Foundation::RECT;

pub(super) fn full(size: [u32; 2]) -> RECT {
    RECT {
        left: 0,
        top: 0,
        right: size[0] as i32,
        bottom: size[1] as i32,
    }
}

pub(super) fn merge(rects: impl IntoIterator<Item = RECT>, size: [u32; 2]) -> Vec<RECT> {
    let mut result: Vec<RECT> = Vec::new();
    for mut rect in rects {
        rect.left = rect.left.max(0);
        rect.top = rect.top.max(0);
        rect.right = rect.right.min(size[0] as i32);
        rect.bottom = rect.bottom.min(size[1] as i32);
        if rect.left >= rect.right || rect.top >= rect.bottom {
            continue;
        }
        let mut i = 0;
        while i < result.len() {
            let other = result[i];
            if rect.left <= other.right
                && other.left <= rect.right
                && rect.top <= other.bottom
                && other.top <= rect.bottom
            {
                rect = union(rect, result.swap_remove(i));
                i = 0;
            } else {
                i += 1;
            }
        }
        result.push(rect);
    }
    // Avoid many tiny draws when desktop metadata is highly fragmented.
    if result.len() > 16 {
        vec![result.into_iter().reduce(union).unwrap()]
    } else {
        result
    }
}

fn union(a: RECT, b: RECT) -> RECT {
    RECT {
        left: a.left.min(b.left),
        top: a.top.min(b.top),
        right: a.right.max(b.right),
        bottom: a.bottom.max(b.bottom),
    }
}

pub(super) fn cleanup(rects: &[RECT], size: [u32; 2]) -> Vec<RECT> {
    // Horizontal anchors/core search reach +/-4. Stroke support reaches
    // +/-16 rows (including its +/-1 horizontal core neighborhood).
    merge(
        rects.iter().map(|r| RECT {
            left: r.left - 4,
            right: r.right + 4,
            top: r.top - 16,
            bottom: r.bottom + 16,
        }),
        size,
    )
}

fn axis(lo: i32, hi: i32, source: u32, target: u32, radius: f64) -> (i32, i32) {
    let scale = source as f64 / target as f64;
    // Source centers are (out + .5) * scale - .5. Outward rounding
    // plus one pixel conservatively includes float32 boundary rounding.
    (
        ((lo as f64 - radius) / scale).floor() as i32 - 1,
        ((hi as f64 + radius) / scale).ceil() as i32 + 1,
    )
}

pub(super) fn resample(
    rects: &[RECT],
    source: [u32; 2],
    target: [u32; 2],
    radius: [Option<f64>; 2],
) -> Vec<RECT> {
    merge(
        rects.iter().map(|r| {
            let (left, right) = radius[0].map_or((r.left, r.right), |rad| {
                axis(r.left, r.right, source[0], target[0], rad)
            });
            let (top, bottom) = radius[1].map_or((r.top, r.bottom), |rad| {
                axis(r.top, r.bottom, source[1], target[1], rad)
            });
            RECT {
                left,
                right,
                top,
                bottom,
            }
        }),
        target,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cleanup_covers_distant_stroke_support_and_clips_edges() {
        let r = cleanup(
            &[RECT {
                left: 20,
                top: 20,
                right: 21,
                bottom: 21,
            }],
            [64, 64],
        );
        assert_eq!(
            r,
            [RECT {
                left: 16,
                top: 4,
                right: 25,
                bottom: 37
            }]
        );
        assert_eq!(cleanup(&[full([8, 8])], [8, 8]), [full([8, 8])]);
        assert!(cleanup(&[], [64, 64]).is_empty());
    }

    #[test]
    fn lanczos_regions_include_every_changed_tap_for_both_axes() {
        for (source, target) in [(31, 17), (17, 31), (31, 31), (128, 7)] {
            let scale = source as f64 / target as f64;
            let radius = 3.0 * scale.max(1.0);
            for changed in 0..source as i32 {
                let (lo, hi) = axis(changed, changed + 1, source, target, radius);
                for out in 0..target as i32 {
                    let center = (out as f64 + 0.5) * scale - 0.5;
                    let first = (center - radius).floor() as i32 + 1;
                    let last = (center + radius).floor() as i32;
                    let reads_changed =
                        (first..=last).any(|tap| tap.clamp(0, source as i32 - 1) == changed);
                    if reads_changed {
                        assert!(out >= lo && out < hi);
                    }
                }
            }
        }
    }

    #[test]
    fn ewa_regions_include_clamped_eight_tap_neighborhood() {
        for changed in 0..17 {
            let (lo, hi) = axis(changed, changed + 1, 17, 43, 4.0);
            for out in 0..43 {
                let base = (((out as f64 + 0.5) * 17.0 / 43.0) - 0.5).floor() as i32;
                if (-3..=4).any(|tap| (base + tap).clamp(0, 16) == changed) {
                    assert!(out >= lo && out < hi);
                }
            }
        }
    }

    #[test]
    fn merge_retains_disjoint_regions_and_joins_transitive_overlaps() {
        let rect = |x| RECT {
            left: x,
            top: 0,
            right: x + 10,
            bottom: 10,
        };
        assert_eq!(merge([rect(0), rect(20)], [100, 100]).len(), 2);
        assert_eq!(
            merge([rect(0), rect(20), rect(10)], [100, 100]),
            [RECT {
                left: 0,
                top: 0,
                right: 30,
                bottom: 10
            }]
        );
    }
}
