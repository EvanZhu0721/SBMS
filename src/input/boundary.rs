//! Pure cursor-boundary geometry for native mapping.
//!
//! This module never calls Win32 input APIs (no cursor movement, hooks or
//! `ClipCursor`): it only classifies screen points against the logical monitor
//! layout and computes correction targets, so every rule is unit-testable.

use std::sync::Arc;

use windows::Win32::Foundation::{POINT, RECT};

/// Physical target and virtual source rectangles of one active input guard.
#[derive(Clone, Copy, Debug)]
pub(super) struct GuardRects {
    pub(super) target: RECT,
    pub(super) source: RECT,
}

impl PartialEq for GuardRects {
    fn eq(&self, other: &Self) -> bool {
        rect_eq(self.target, other.target) && rect_eq(self.source, other.source)
    }
}

/// Validated boundary geometry for one registration generation.
pub(super) struct Boundary {
    allowed: Vec<RECT>,
    forbidden: Vec<RECT>,
    guards: Vec<GuardRects>,
    clip: RECT,
}

impl Boundary {
    /// Build boundary geometry from the logical monitor layout and the target
    /// and source rectangles registered by the active input guards.
    ///
    /// Returns `None` (fail open: no boundary at all) when the registration is
    /// invalid or stale: empty layout or guards, empty rectangles, registered
    /// rectangles missing from the layout, or overlapping registrations.
    pub(super) fn build(layout: &[RECT], guards: &[GuardRects]) -> Option<Boundary> {
        if layout.is_empty() || guards.is_empty() {
            return None;
        }
        if layout.iter().any(|monitor| !nonempty(*monitor)) {
            return None;
        }
        let mut registered: Vec<RECT> = Vec::new();
        for guard in guards {
            for rect in [guard.target, guard.source] {
                if !nonempty(rect) {
                    return None;
                }
                if !layout.iter().any(|monitor| rect_eq(*monitor, rect)) {
                    return None;
                }
                if registered.iter().any(|other| overlaps(*other, rect)) {
                    return None;
                }
                registered.push(rect);
            }
        }
        let forbidden: Vec<RECT> = guards.iter().map(|guard| guard.target).collect();
        let allowed: Vec<RECT> = layout
            .iter()
            .copied()
            .filter(|monitor| !forbidden.iter().any(|target| overlaps(*target, *monitor)))
            .collect();
        if allowed.is_empty() {
            return None;
        }
        let clip = bounding_box(&allowed);
        Some(Boundary {
            allowed,
            forbidden,
            guards: guards.to_vec(),
            clip,
        })
    }

    /// The single `ClipCursor` rectangle: the bounding box of the allowed
    /// monitors. It may contain harmless gaps (the cursor cannot rest in
    /// monitor-less space). When it covers no forbidden target rectangle it is
    /// a complete hard boundary; otherwise the low-level hook corrects the
    /// remaining forbidden regions (and covers the loss of the global clip).
    pub(super) fn clip_rect(&self) -> RECT {
        self.clip
    }

    /// The logical workspace with each virtual source projected onto its
    /// physical output. Hidden source desktops never become screenshot targets.
    pub(super) fn screenshot_screens(&self) -> Vec<super::navigation::Screen> {
        self.allowed
            .iter()
            .map(|logical| super::navigation::Screen {
                logical: *logical,
                physical: self
                    .guards
                    .iter()
                    .find(|guard| rect_eq(guard.source, *logical))
                    .map_or(*logical, |guard| guard.target),
            })
            .collect()
    }

    pub(super) fn is_forbidden(&self, point: POINT) -> bool {
        self.forbidden
            .iter()
            .any(|rect| contains_point(*rect, point))
    }

    /// Correct a forbidden movement attempt: clamp the attempted coordinates to
    /// the previous allowed monitor (preserving the tangent along its edge) and
    /// fall back to the nearest allowed monitor when the previous one is
    /// unknown. Never teleports to a center point.
    pub(super) fn correct_movement(&self, attempted: POINT, previous: Option<POINT>) -> POINT {
        if let Some(rect) = previous.and_then(|point| {
            self.allowed
                .iter()
                .find(|monitor| contains_point(**monitor, point))
                .copied()
        }) {
            return clamp_point(rect, attempted);
        }
        self.warp_to_allowed(attempted)
    }

    /// Nearest allowed destination for a discrete warp (capture release, pause
    /// resume): points that are not forbidden are left untouched.
    pub(super) fn warp_to_allowed(&self, point: POINT) -> POINT {
        if !self.is_forbidden(point) {
            return point;
        }
        let nearest = self
            .allowed
            .iter()
            .enumerate()
            .min_by_key(|(index, rect)| (distance_sq(**rect, point), rect.left, rect.top, *index))
            .map(|(_, rect)| *rect)
            .expect("allowed monitors are nonempty");
        clamp_point(nearest, point)
    }
}

/// Desired clip state for the single `ClipCursor` owner.
#[derive(Clone, Copy, Debug)]
pub(super) enum ClipAction {
    /// First own clip: preserve the external baseline, then set our rectangle.
    SaveBaselineThenSet(RECT),
    /// Swap our own clip rectangle; the baseline is kept untouched.
    Set(RECT),
    /// Nothing of ours remains: restore the preserved external baseline.
    RestoreBaseline,
    /// Nothing of ours and no baseline to restore.
    None,
}

impl PartialEq for ClipAction {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (ClipAction::SaveBaselineThenSet(left), ClipAction::SaveBaselineThenSet(right))
            | (ClipAction::Set(left), ClipAction::Set(right)) => rect_eq(*left, *right),
            (ClipAction::RestoreBaseline, ClipAction::RestoreBaseline)
            | (ClipAction::None, ClipAction::None) => true,
            _ => false,
        }
    }
}

/// Priority: captured source clip; the screenshot pause releases the native
/// boundary; the native boundary applies when no capture and no pause are
/// active. `boundary` is the boundary clip rectangle while the boundary is
/// live. `has_baseline` tracks whether the external baseline was preserved.
pub(super) fn clip_action(
    capture: Option<RECT>,
    paused: bool,
    boundary: Option<RECT>,
    has_baseline: bool,
) -> ClipAction {
    let desired = capture.or(if paused { None } else { boundary });
    match (has_baseline, desired) {
        (false, Some(rect)) => ClipAction::SaveBaselineThenSet(rect),
        (true, Some(rect)) => ClipAction::Set(rect),
        (true, None) => ClipAction::RestoreBaseline,
        (false, None) => ClipAction::None,
    }
}

/// Global registration state shared by all input guards (multiple mappers).
pub(super) struct BoundaryRegistry {
    guards: Vec<(usize, GuardRects)>,
    layout: Vec<RECT>,
    layout_valid: bool,
    pause_owners: usize,
    snapshot: Option<Arc<Boundary>>,
}

impl BoundaryRegistry {
    pub(super) const fn new() -> Self {
        Self {
            guards: Vec::new(),
            layout: Vec::new(),
            layout_valid: false,
            pause_owners: 0,
            snapshot: None,
        }
    }

    pub(super) fn register(&mut self, window: usize, rects: GuardRects) {
        self.guards.retain(|(key, _)| *key != window);
        self.guards.push((window, rects));
        self.rebuild();
    }

    pub(super) fn unregister(&mut self, window: usize) {
        self.guards.retain(|(key, _)| *key != window);
        self.rebuild();
    }

    pub(super) fn set_layout(&mut self, layout: Vec<RECT>) {
        self.layout = layout;
        self.layout_valid = true;
        self.rebuild();
    }

    /// A display-layout mutation invalidates every guard's geometry; the
    /// boundary stays off until a fresh layout passes validation again.
    pub(super) fn invalidate(&mut self) {
        self.layout.clear();
        self.layout_valid = false;
        self.rebuild();
    }

    pub(super) fn pause(&mut self) {
        self.pause_owners = self.pause_owners.saturating_add(1);
    }

    pub(super) fn resume(&mut self) {
        self.pause_owners = self.pause_owners.saturating_sub(1);
    }

    pub(super) fn has_pause(&self) -> bool {
        self.pause_owners > 0
    }

    /// Current boundary geometry, ignoring the screenshot pause (callers fold
    /// `has_pause` into their own decisions).
    pub(super) fn snapshot(&self) -> Option<Arc<Boundary>> {
        self.snapshot.clone()
    }

    fn rebuild(&mut self) {
        self.snapshot = if self.layout_valid {
            let guards: Vec<GuardRects> = self.guards.iter().map(|(_, rects)| *rects).collect();
            Boundary::build(&self.layout, &guards).map(Arc::new)
        } else {
            None
        };
    }
}

fn nonempty(rect: RECT) -> bool {
    rect.right > rect.left && rect.bottom > rect.top
}

fn rect_eq(left: RECT, right: RECT) -> bool {
    left.left == right.left
        && left.top == right.top
        && left.right == right.right
        && left.bottom == right.bottom
}

/// Nonzero-area intersection (rectangles are half-open).
fn overlaps(left: RECT, right: RECT) -> bool {
    left.left < right.right
        && right.left < left.right
        && left.top < right.bottom
        && right.top < left.bottom
}

/// Half-open containment: `[left, right) x [top, bottom)`.
fn contains_point(rect: RECT, point: POINT) -> bool {
    point.x >= rect.left && point.x < rect.right && point.y >= rect.top && point.y < rect.bottom
}

fn clamp_point(rect: RECT, point: POINT) -> POINT {
    POINT {
        x: point.x.clamp(rect.left, rect.right - 1),
        y: point.y.clamp(rect.top, rect.bottom - 1),
    }
}

fn bounding_box(rects: &[RECT]) -> RECT {
    let mut iter = rects.iter().copied();
    let first = iter.next().expect("bounding box of a nonempty list");
    let mut box_rect = first;
    for rect in iter {
        box_rect.left = box_rect.left.min(rect.left);
        box_rect.top = box_rect.top.min(rect.top);
        box_rect.right = box_rect.right.max(rect.right);
        box_rect.bottom = box_rect.bottom.max(rect.bottom);
    }
    box_rect
}

/// Squared distance from a point to the nearest pixel of a half-open rectangle.
/// Endpoint arithmetic happens in `i64` before subtracting and the squared
/// distance accumulates in `u128`, so extreme coordinates cannot overflow.
fn distance_sq(rect: RECT, point: POINT) -> u128 {
    let x = i64::from(point.x);
    let y = i64::from(point.y);
    let left = i64::from(rect.left);
    let top = i64::from(rect.top);
    let right_edge = i64::from(rect.right) - 1;
    let bottom_edge = i64::from(rect.bottom) - 1;
    let dx = if x < left {
        left - x
    } else if x > right_edge {
        x - right_edge
    } else {
        0
    };
    let dy = if y < top {
        top - y
    } else if y > bottom_edge {
        y - bottom_edge
    } else {
        0
    };
    let dx = dx as u128;
    let dy = dy as u128;
    dx * dx + dy * dy
}

#[cfg(test)]
mod tests {
    use super::{Boundary, BoundaryRegistry, ClipAction, GuardRects, clip_action, overlaps};
    use windows::Win32::Foundation::{POINT, RECT};

    fn rect(left: i32, top: i32, right: i32, bottom: i32) -> RECT {
        RECT {
            left,
            top,
            right,
            bottom,
        }
    }

    fn point(x: i32, y: i32) -> POINT {
        POINT { x, y }
    }

    /// Whether the clip bounding box is free of forbidden target rectangles:
    /// the geometry where a single ClipCursor rectangle is a complete boundary.
    fn clip_is_disjoint_from_forbidden(boundary: &Boundary) -> bool {
        !boundary
            .forbidden
            .iter()
            .any(|target| overlaps(*target, boundary.clip))
    }

    /// Screenshot layout: 2 at the upper left of 1, 3 below 1; 2 is the mapped
    /// physical target, 1 and 3 stay usable.
    fn screenshot_layout() -> [RECT; 3] {
        [
            rect(0, 0, 1920, 1080),
            rect(-1280, -720, 0, 0),
            rect(0, 1080, 1920, 2160),
        ]
    }

    #[test]
    fn screenshot_layout_allowing_1_and_3_uses_clip_only() {
        let layout = screenshot_layout();
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[0],
            }],
        )
        .expect("valid registration");
        assert!(clip_is_disjoint_from_forbidden(&boundary));
        let clip = boundary.clip_rect();
        assert_eq!(
            (clip.left, clip.top, clip.right, clip.bottom),
            (0, 0, 1920, 2160)
        );
        assert!(boundary.is_forbidden(point(-1, -1)));
        assert!(boundary.is_forbidden(point(-1280, -720)));
        assert!(!boundary.is_forbidden(point(0, 0)));
        assert!(!boundary.is_forbidden(point(500, 1500)));
    }

    #[test]
    fn forbidden_between_allowed_monitors_requires_hook_correction() {
        let layout = [
            rect(0, 0, 1920, 1080),
            rect(640, 1080, 1280, 1380),
            rect(0, 1380, 1920, 1980),
        ];
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[2],
            }],
        )
        .expect("valid registration");
        assert!(!clip_is_disjoint_from_forbidden(&boundary));
        let clip = boundary.clip_rect();
        assert_eq!(
            (clip.left, clip.top, clip.right, clip.bottom),
            (0, 0, 1920, 1980)
        );
    }

    #[test]
    fn movement_correction_preserves_tangent_on_previous_monitor() {
        let layout = [
            rect(0, 0, 1920, 1080),
            rect(640, 1080, 1280, 1380),
            rect(0, 1380, 1920, 1980),
        ];
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[2],
            }],
        )
        .expect("valid registration");
        // Exiting through the top edge of 3 keeps the x coordinate.
        assert_eq!(
            boundary.correct_movement(point(700, 1200), Some(point(700, 1500))),
            point(700, 1380)
        );
        // Exiting through the bottom edge of 1 keeps the x coordinate.
        assert_eq!(
            boundary.correct_movement(point(700, 1200), Some(point(700, 500))),
            point(700, 1079)
        );
        // A diagonal entry clamps to the touched corner of the previous monitor.
        assert_eq!(
            boundary.correct_movement(point(641, 1081), Some(point(641, 1079))),
            point(641, 1079)
        );
    }

    #[test]
    fn correction_falls_back_to_nearest_allowed_monitor() {
        let layout = [
            rect(0, 0, 1920, 1080),
            rect(640, 1080, 1280, 1380),
            rect(0, 1380, 1920, 1980),
        ];
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[2],
            }],
        )
        .expect("valid registration");
        // Without a usable previous monitor the nearest allowed monitor wins.
        assert_eq!(
            boundary.correct_movement(point(700, 1200), Some(point(700, 1250))),
            point(700, 1079)
        );
        assert_eq!(
            boundary.correct_movement(point(700, 1200), None),
            point(700, 1079)
        );
    }

    #[test]
    fn negative_and_unequal_layouts_use_half_open_edges() {
        // Unequal sizes and negative coordinates; the forbidden target sits
        // between the two allowed monitors, so hook correction applies.
        let layout = [
            rect(-2560, -1440, 0, -480),
            rect(-1600, -480, -320, 0),
            rect(-2560, 0, 0, 720),
        ];
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[0],
            }],
        )
        .expect("valid registration");
        assert!(!clip_is_disjoint_from_forbidden(&boundary));
        // Tangent preservation with negative coordinates.
        assert_eq!(
            boundary.correct_movement(point(-1000, -200), Some(point(-1000, -1000))),
            point(-1000, -481)
        );
        // The monitor below owns the exit and clamps to its first row.
        assert_eq!(
            boundary.correct_movement(point(-1000, -200), Some(point(-1000, 100))),
            point(-1000, 0)
        );
        // Half-open clamp: the last pixel of the previous monitor is kept.
        assert_eq!(
            boundary.correct_movement(point(-1599, -479), Some(point(-1000, -1000))),
            point(-1599, -481)
        );
        // Nearest allowed monitor fallback still lands on an edge pixel.
        assert_eq!(
            boundary.warp_to_allowed(point(-1000, -200)),
            point(-1000, 0)
        );
    }

    #[test]
    fn clip_only_layout_with_negative_target_outside_bbox() {
        // The forbidden target lies completely right of the allowed bounding
        // box: the single clip rectangle is a complete boundary.
        let layout = [
            rect(-2560, -1440, 0, 0),
            rect(0, 0, 1920, 1080),
            rect(-640, 0, 0, 720),
        ];
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[0],
            }],
        )
        .expect("valid registration");
        assert!(clip_is_disjoint_from_forbidden(&boundary));
        let clip = boundary.clip_rect();
        assert_eq!(
            (clip.left, clip.top, clip.right, clip.bottom),
            (-2560, -1440, 0, 720)
        );
    }

    #[test]
    fn edge_pixels_are_half_open_and_preserved() {
        let layout = screenshot_layout();
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[0],
            }],
        )
        .expect("valid registration");
        // Half-open: the corner pixel (0, 0) is allowed, (-1, -1) is forbidden.
        assert!(!boundary.is_forbidden(point(0, 0)));
        assert!(!boundary.is_forbidden(point(0, 2159)));
        assert!(boundary.is_forbidden(point(-1, -1)));
        // The last allowed pixel is preserved and never warped.
        assert_eq!(
            boundary.warp_to_allowed(point(1919, 2159)),
            point(1919, 2159)
        );
        // A forbidden attempt clamps to the adjacent edge pixel of the previous
        // allowed monitor; unknown previous state falls back to the nearest one.
        assert_eq!(
            boundary.correct_movement(point(-5, -5), Some(point(5, 5))),
            point(0, 0)
        );
        assert_eq!(
            boundary.correct_movement(point(-5, -5), Some(point(5, 1500))),
            point(0, 1080)
        );
    }

    #[test]
    fn unmapped_displays_remain_usable_and_never_forbidden() {
        let mut layout = screenshot_layout().to_vec();
        layout.push(rect(3000, 0, 4920, 1080));
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[0],
            }],
        )
        .expect("valid registration");
        assert!(!boundary.is_forbidden(point(3500, 500)));
        let clip = boundary.clip_rect();
        assert!((clip.left, clip.top, clip.right, clip.bottom) == (0, 0, 4920, 2160));
    }

    #[test]
    fn warp_keeps_already_allowed_points_untouched() {
        let layout = screenshot_layout();
        let boundary = Boundary::build(
            &layout,
            &[GuardRects {
                target: layout[1],
                source: layout[0],
            }],
        )
        .expect("valid registration");
        assert_eq!(boundary.warp_to_allowed(point(5, 5)), point(5, 5));
        assert_eq!(
            boundary.warp_to_allowed(point(1919, 1079)),
            point(1919, 1079)
        );
    }

    #[test]
    fn invalid_registrations_fail_open() {
        let layout = screenshot_layout();
        let guards = [GuardRects {
            target: layout[1],
            source: layout[0],
        }];
        // Empty layout or guard list.
        assert!(Boundary::build(&[], &guards).is_none());
        assert!(Boundary::build(&layout, &[]).is_none());
        // Empty rectangle.
        assert!(
            Boundary::build(
                &layout,
                &[GuardRects {
                    target: rect(0, 0, 0, 0),
                    source: layout[0],
                }],
            )
            .is_none()
        );
        // Registered rectangle missing from the layout (stale topology).
        assert!(
            Boundary::build(
                &layout,
                &[GuardRects {
                    target: rect(5, 5, 10, 10),
                    source: layout[0],
                }],
            )
            .is_none()
        );
        // Overlapping registrations (target equals its own source).
        assert!(
            Boundary::build(
                &layout,
                &[GuardRects {
                    target: layout[0],
                    source: layout[0],
                }],
            )
            .is_none()
        );
        // Two guards sharing one target display.
        assert!(Boundary::build(&layout, &[guards[0], guards[0]]).is_none());
    }

    #[test]
    fn registry_guard_add_remove_updates_boundary() {
        let layout = screenshot_layout();
        let mut registry = BoundaryRegistry::new();
        assert!(registry.snapshot().is_none());
        registry.set_layout(layout.to_vec());
        assert!(registry.snapshot().is_none(), "no guards, no boundary");
        registry.register(
            11,
            GuardRects {
                target: layout[1],
                source: layout[0],
            },
        );
        let boundary = registry.snapshot().expect("one guard");
        assert!(clip_is_disjoint_from_forbidden(&boundary));
        // A second guard on the same target display is invalid: fail open.
        registry.register(
            22,
            GuardRects {
                target: layout[1],
                source: layout[2],
            },
        );
        assert!(registry.snapshot().is_none());
        registry.unregister(22);
        assert!(registry.snapshot().is_some());
        registry.unregister(11);
        assert!(
            registry.snapshot().is_none(),
            "empty guards restore baseline"
        );
    }

    #[test]
    fn registry_invalidates_until_layout_is_refreshed() {
        let layout = screenshot_layout();
        let mut registry = BoundaryRegistry::new();
        registry.set_layout(layout.to_vec());
        registry.register(
            11,
            GuardRects {
                target: layout[1],
                source: layout[0],
            },
        );
        assert!(registry.snapshot().is_some());
        registry.invalidate();
        assert!(
            registry.snapshot().is_none(),
            "display change releases boundary"
        );
        // Re-registration without a fresh layout must not revive stale geometry.
        registry.unregister(11);
        registry.register(
            12,
            GuardRects {
                target: layout[1],
                source: layout[0],
            },
        );
        assert!(registry.snapshot().is_none());
        registry.set_layout(layout.to_vec());
        assert!(
            registry.snapshot().is_some(),
            "refresh restores validated geometry"
        );
    }

    #[test]
    fn registry_pause_balances_across_mappers() {
        let layout = screenshot_layout();
        let mut registry = BoundaryRegistry::new();
        registry.set_layout(layout.to_vec());
        registry.register(
            11,
            GuardRects {
                target: layout[1],
                source: layout[0],
            },
        );
        assert!(!registry.has_pause());
        registry.pause();
        registry.pause();
        assert!(registry.has_pause());
        registry.resume();
        assert!(registry.has_pause(), "one owner still holds the pause");
        registry.resume();
        assert!(!registry.has_pause());
        registry.resume();
        assert!(
            !registry.has_pause(),
            "unbalanced resume must not underflow"
        );
    }

    #[test]
    fn clip_action_transitions_preserve_baseline_across_swaps() {
        let source = rect(0, 0, 1920, 1080);
        let clip = rect(-10, -10, 2000, 2000);
        // First own clip preserves the external baseline.
        assert_eq!(
            clip_action(Some(source), false, Some(clip), false),
            ClipAction::SaveBaselineThenSet(source)
        );
        // Capture -> boundary swap keeps the baseline untouched.
        assert_eq!(
            clip_action(None, false, Some(clip), true),
            ClipAction::Set(clip)
        );
        assert_eq!(
            clip_action(Some(source), false, Some(clip), true),
            ClipAction::Set(source)
        );
    }

    #[test]
    fn clip_action_screenshot_pause_releases_only_the_native_boundary() {
        let source = rect(0, 0, 1920, 1080);
        let clip = rect(-10, -10, 2000, 2000);
        // Pause releases the native boundary back to the baseline.
        assert_eq!(
            clip_action(None, true, Some(clip), true),
            ClipAction::RestoreBaseline
        );
        // A captured source clip survives the pause.
        assert_eq!(
            clip_action(Some(source), true, Some(clip), true),
            ClipAction::Set(source)
        );
    }

    #[test]
    fn clip_action_balances_down_to_the_baseline() {
        let source = rect(0, 0, 1920, 1080);
        let clip = rect(-10, -10, 2000, 2000);
        // Empty guards restore the baseline exactly once.
        assert_eq!(
            clip_action(None, false, None, true),
            ClipAction::RestoreBaseline
        );
        assert_eq!(clip_action(None, false, None, false), ClipAction::None);
        // Full lifecycle: boundary -> capture -> boundary -> stop.
        assert_eq!(
            clip_action(None, false, Some(clip), false),
            ClipAction::SaveBaselineThenSet(clip)
        );
        assert_eq!(
            clip_action(Some(source), false, Some(clip), true),
            ClipAction::Set(source)
        );
        assert_eq!(
            clip_action(None, false, Some(clip), true),
            ClipAction::Set(clip)
        );
        assert_eq!(
            clip_action(None, false, None, true),
            ClipAction::RestoreBaseline
        );
    }
}
