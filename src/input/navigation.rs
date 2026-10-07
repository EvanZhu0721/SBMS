//! Screenshot navigation in the visible workspace. Pure geometry only.
//!
//! Movement within a screen stays native. At an edge, its logical neighbour
//! determines the destination, even when the two physical outputs are far apart.
use windows::Win32::Foundation::{POINT, RECT};

#[derive(Clone, Copy, Debug)]
pub(super) struct Screen {
    pub(super) logical: RECT,
    pub(super) physical: RECT,
}

pub(super) struct Navigation {
    screens: Vec<Screen>,
    current: usize,
    held: u8,
    last_warp_time: Option<u32>,
}

#[derive(Clone, Copy)]
enum Edge {
    Left,
    Right,
    Top,
    Bottom,
}

impl Navigation {
    pub(super) fn new(screens: Vec<Screen>, cursor: POINT, held: u8) -> Option<Self> {
        if screens
            .iter()
            .any(|s| !valid(s.logical) || !valid(s.physical))
        {
            return None;
        }
        for (i, a) in screens.iter().enumerate() {
            if screens[i + 1..]
                .iter()
                .any(|b| overlaps(a.logical, b.logical) || overlaps(a.physical, b.physical))
            {
                return None;
            }
        }
        // A new session starts in the normal logical workspace. Physical
        // lookup also permits an already completed handoff from another guard.
        let current = screens
            .iter()
            .position(|s| contains(s.logical, cursor))
            .or_else(|| screens.iter().position(|s| contains(s.physical, cursor)))?;
        Some(Self {
            screens,
            current,
            held,
            last_warp_time: None,
        })
    }

    pub(super) fn clip(&self) -> RECT {
        self.screens[self.current].physical
    }

    pub(super) fn handoff(&self, cursor: POINT) -> POINT {
        let screen = self.screens[self.current];
        let point = if contains(screen.logical, cursor) {
            map(cursor, screen.logical, screen.physical)
        } else {
            clamp(screen.physical, cursor)
        };
        // Enter one pixel inside the output. Even a clip-truncated first move
        // then has an outward direction at an outer desktop edge.
        inset(screen.physical, point)
    }

    pub(super) fn return_point(&self, cursor: POINT) -> POINT {
        self.screens
            .iter()
            .find(|s| contains(s.physical, cursor))
            .map(|s| map(cursor, s.physical, s.logical))
            .unwrap_or(cursor)
    }

    pub(super) fn button(&mut self, bit: u8, down: bool) {
        if down {
            self.held |= bit;
        } else {
            self.held &= !bit;
        }
    }

    pub(super) fn sync_buttons(&mut self, held: u8) {
        self.held = held;
    }

    pub(super) fn accepts_motion(&self, time: u32) -> bool {
        self.last_warp_time
            .is_none_or(|last| (time.wrapping_sub(last) as i32) > 0)
    }

    pub(super) fn warped_at(&mut self, time: u32) {
        self.last_warp_time = Some(time);
    }

    /// Raw deltas provide direction only, never speed/acceleration. They are
    /// needed when Windows has clipped both the old and new native positions
    /// to the same edge pixel (especially when changing direction at a corner).
    pub(super) fn raw_edge_move(&mut self, delta: POINT, cursor: POINT) -> POINT {
        let clip = self.clip();
        if !contains(clip, cursor) {
            return cursor;
        }
        let mut attempted = cursor;
        if (cursor.x == clip.left && delta.x < 0) || (cursor.x == clip.right - 1 && delta.x > 0) {
            attempted.x = cursor.x.saturating_add(delta.x.signum());
        }
        if (cursor.y == clip.top && delta.y < 0) || (cursor.y == clip.bottom - 1 && delta.y > 0) {
            attempted.y = cursor.y.saturating_add(delta.y.signum());
        }
        if attempted == cursor {
            cursor
        } else {
            self.move_to(attempted, cursor)
        }
    }

    pub(super) fn move_to(&mut self, attempted: POINT, previous: POINT) -> POINT {
        let screen = self.screens[self.current];
        let previous = clamp(screen.physical, previous);
        let constrained = clamp(screen.physical, attempted);
        let direction = POINT {
            x: attempted.x.saturating_sub(previous.x),
            y: attempted.y.saturating_sub(previous.y),
        };
        if self.held != 0 {
            return constrained;
        }
        let Some((edge, hit)) = exit_edge(screen.physical, previous, attempted, direction) else {
            return constrained;
        };
        let logical_hit = map(hit, screen.physical, screen.logical);
        let next = self.screens.iter().enumerate().find(|(i, candidate)| {
            *i != self.current && adjacent(screen.logical, candidate.logical, logical_hit, edge)
        });
        let Some((index, next)) = next else {
            return constrained;
        };
        // Preserve tangential position and the remainder of a fast move. Stop
        // within the first adjacent screen rather than jumping over a gap.
        let logical_end = map_unbounded(attempted, screen.physical, screen.logical);
        let mut destination = map(
            clamp(next.logical, logical_end),
            next.logical,
            next.physical,
        );
        match edge {
            Edge::Left | Edge::Right => {
                destination.x = inset_axis(destination.x, next.physical.left, next.physical.right)
            }
            Edge::Top | Edge::Bottom => {
                destination.y = inset_axis(destination.y, next.physical.top, next.physical.bottom)
            }
        }
        self.current = index;
        destination
    }
}

fn exit_edge(rect: RECT, from: POINT, to: POINT, direction: POINT) -> Option<(Edge, POINT)> {
    let dx = f64::from(to.x) - f64::from(from.x);
    let dy = f64::from(to.y) - f64::from(from.y);
    let mut first: Option<(f64, Edge)> = None;
    for (edge, outward, reaches, distance, delta) in [
        (
            Edge::Left,
            direction.x < 0,
            to.x <= rect.left,
            f64::from(rect.left) - f64::from(from.x),
            dx,
        ),
        (
            Edge::Right,
            direction.x > 0,
            to.x >= rect.right - 1,
            f64::from(rect.right - 1) - f64::from(from.x),
            dx,
        ),
        (
            Edge::Top,
            direction.y < 0,
            to.y <= rect.top,
            f64::from(rect.top) - f64::from(from.y),
            dy,
        ),
        (
            Edge::Bottom,
            direction.y > 0,
            to.y >= rect.bottom - 1,
            f64::from(rect.bottom - 1) - f64::from(from.y),
            dy,
        ),
    ] {
        if !outward || !reaches {
            continue;
        }
        let fraction = if delta == 0.0 {
            0.0
        } else {
            (distance / delta).clamp(0.0, 1.0)
        };
        if first.is_none_or(|(time, _)| fraction < time) {
            first = Some((fraction, edge));
        }
    }
    first.map(|(time, edge)| {
        (
            edge,
            clamp(
                rect,
                POINT {
                    x: (f64::from(from.x) + dx * time).round() as i32,
                    y: (f64::from(from.y) + dy * time).round() as i32,
                },
            ),
        )
    })
}

fn adjacent(from: RECT, to: RECT, hit: POINT, edge: Edge) -> bool {
    match edge {
        Edge::Left => from.left == to.right && hit.y >= to.top && hit.y < to.bottom,
        Edge::Right => from.right == to.left && hit.y >= to.top && hit.y < to.bottom,
        Edge::Top => from.top == to.bottom && hit.x >= to.left && hit.x < to.right,
        Edge::Bottom => from.bottom == to.top && hit.x >= to.left && hit.x < to.right,
    }
}

fn map(point: POINT, from: RECT, to: RECT) -> POINT {
    clamp(to, map_unbounded(clamp(from, point), from, to))
}

fn map_unbounded(point: POINT, from: RECT, to: RECT) -> POINT {
    fn axis(value: i32, start: i32, end: i32, target: i32, limit: i32) -> i32 {
        // Inclusive endpoints, rounded to the nearest pixel. Rounding avoids
        // cumulative tangential drift when repeatedly crossing scaled screens.
        // Widen before arithmetic to keep extreme desktop coordinates safe.
        let source_span = (i128::from(end) - i128::from(start) - 1).max(1);
        let target_span = i128::from(limit) - i128::from(target) - 1;
        let numerator = (i128::from(value) - i128::from(start)) * target_span;
        (i128::from(target) + (numerator + source_span / 2).div_euclid(source_span))
            .clamp(i128::from(i32::MIN), i128::from(i32::MAX)) as i32
    }
    POINT {
        x: axis(point.x, from.left, from.right, to.left, to.right),
        y: axis(point.y, from.top, from.bottom, to.top, to.bottom),
    }
}

fn inset_axis(value: i32, start: i32, end: i32) -> i32 {
    if i64::from(end) - i64::from(start) > 2 {
        value.clamp(start + 1, end - 2)
    } else {
        value.clamp(start, end - 1)
    }
}

fn inset(rect: RECT, point: POINT) -> POINT {
    POINT {
        x: inset_axis(point.x, rect.left, rect.right),
        y: inset_axis(point.y, rect.top, rect.bottom),
    }
}

fn valid(rect: RECT) -> bool {
    rect.left < rect.right && rect.top < rect.bottom
}
fn contains(rect: RECT, point: POINT) -> bool {
    point.x >= rect.left && point.x < rect.right && point.y >= rect.top && point.y < rect.bottom
}
fn clamp(rect: RECT, point: POINT) -> POINT {
    POINT {
        x: point.x.clamp(rect.left, rect.right - 1),
        y: point.y.clamp(rect.top, rect.bottom - 1),
    }
}
fn overlaps(a: RECT, b: RECT) -> bool {
    a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom
}

#[cfg(test)]
mod tests {
    use super::*;
    fn r(left: i32, top: i32, right: i32, bottom: i32) -> RECT {
        RECT {
            left,
            top,
            right,
            bottom,
        }
    }
    fn p(x: i32, y: i32) -> POINT {
        POINT { x, y }
    }
    fn screens() -> Vec<Screen> {
        vec![
            Screen {
                logical: r(0, 0, 400, 200),
                physical: r(0, 0, 400, 200),
            },
            Screen {
                logical: r(20, 200, 380, 400),
                physical: r(-200, -100, 0, 0),
            },
        ]
    }
    fn nav() -> Navigation {
        Navigation::new(screens(), p(200, 300), 0).unwrap()
    }

    #[test]
    fn handoff_and_up_follow_virtual_layout() {
        let mut nav = nav();
        let handoff = nav.handoff(p(200, 300));
        assert!(contains(r(-200, -100, 0, 0), handoff));
        let landed = nav.move_to(p(-100, -100), p(-100, -99));
        assert_eq!(nav.clip(), r(0, 0, 400, 200));
        assert!((198..=202).contains(&landed.x));
        assert_eq!(landed.y, 198);
        let returned = nav.move_to(p(landed.x, 200), landed);
        assert_eq!(nav.clip(), r(-200, -100, 0, 0));
        assert_eq!(returned.y, -99);
    }
    #[test]
    fn interior_native_motion_is_unchanged() {
        let mut nav = nav();
        for (a, b) in [(p(-80, -40), p(-100, -50)), (p(-120, -80), p(-80, -40))] {
            assert_eq!(nav.move_to(a, b), a);
        }
    }
    #[test]
    fn wrong_physical_neighbour_is_blocked() {
        let mut nav = nav();
        assert_eq!(nav.move_to(p(100, 50), p(-2, -50)), p(-1, -1));
        assert_eq!(nav.clip(), r(-200, -100, 0, 0));
    }
    #[test]
    fn drag_never_crosses_and_release_allows_navigation() {
        let mut nav = nav();
        nav.button(1, true);
        assert_eq!(nav.move_to(p(-100, -120), p(-100, -99)), p(-100, -100));
        nav.button(2, true);
        nav.button(1, false);
        assert_eq!(nav.move_to(p(-100, -120), p(-100, -100)), p(-100, -100));
        nav.button(2, false);
        assert!(contains(
            r(0, 0, 400, 200),
            nav.move_to(p(-100, -120), p(-100, -100))
        ));
    }
    #[test]
    fn non_overlapping_edge_segment_is_a_wall() {
        let mut nav = Navigation::new(screens(), p(5, 100), 0).unwrap();
        assert_eq!(nav.move_to(p(5, 220), p(5, 190)), p(5, 199));
        assert_eq!(nav.clip(), r(0, 0, 400, 200));
    }
    #[test]
    fn fast_diagonal_uses_first_edge_intersection() {
        let mut nav = Navigation::new(screens(), p(5, 100), 0).unwrap();
        // The endpoint is over screen 3, but the path hits the unconnected
        // portion of screen 1's lower edge first. It must not teleport.
        assert_eq!(nav.move_to(p(200, 4000), p(5, 198)), p(200, 199));
        assert_eq!(nav.clip(), r(0, 0, 400, 200));
    }
    #[test]
    fn return_uses_current_display_not_starting_display() {
        let mut nav = nav();
        assert_eq!(nav.return_point(p(-200, -100)), p(20, 200));
        let landed = nav.move_to(p(-100, -110), p(-100, -90));
        assert_eq!(nav.return_point(landed), landed);
    }
    #[test]
    fn initial_edge_is_inset_and_stationary_point_does_not_teleport() {
        let mut nav = nav();
        assert_eq!(nav.handoff(p(20, 200)), p(-199, -99));
        assert_eq!(nav.move_to(p(-100, -100), p(-100, -100)), p(-100, -100));
    }
    #[test]
    fn no_ping_pong_after_transition() {
        let mut nav = nav();
        let a = nav.move_to(p(-100, -100), p(-100, -99));
        assert_eq!(nav.move_to(a, a), a);
        assert_eq!(nav.move_to(p(a.x, a.y - 1), a), p(a.x, a.y - 1));
    }

    #[test]
    fn repeated_scaled_crossings_do_not_drift_sideways() {
        let mut nav = nav();
        let mut current = p(-100, -99);
        for _ in 0..100 {
            let upper = nav.move_to(p(current.x, -100), current);
            current = nav.move_to(p(upper.x, 199), upper);
            assert_eq!(current.x, -100);
        }
    }

    #[test]
    fn left_and_right_transfer_between_two_mapped_outputs() {
        let mut layout = screens();
        layout.push(Screen {
            logical: r(-300, 200, 20, 400),
            physical: r(-700, 0, -400, 200),
        });
        let mut nav = Navigation::new(layout, p(200, 300), 0).unwrap();
        let left = nav.move_to(p(-200, -50), p(-199, -50));
        assert_eq!(nav.clip(), r(-700, 0, -400, 200));
        assert_eq!(left.x, -402);
        let back = nav.move_to(p(-401, left.y), left);
        assert_eq!(nav.clip(), r(-200, -100, 0, 0));
        assert_eq!(back.x, -199);
        assert_eq!(back.y, -50);
    }

    #[test]
    fn held_button_at_start_and_missed_release_are_reconciled() {
        let mut nav = Navigation::new(screens(), p(200, 300), 1).unwrap();
        assert_eq!(nav.move_to(p(-100, -100), p(-100, -99)), p(-100, -100));
        nav.sync_buttons(0);
        let upper = nav.raw_edge_move(p(0, -1), p(-100, -100));
        assert!(contains(r(0, 0, 400, 200), upper));
    }

    #[test]
    fn starting_on_ordinary_screen_can_enter_mapped_output() {
        let mut nav = Navigation::new(screens(), p(200, 100), 0).unwrap();
        let lower = nav.move_to(p(200, 199), p(200, 195));
        assert!(contains(r(-200, -100, 0, 0), lower));
        assert!(contains(r(20, 200, 380, 400), nav.return_point(lower)));
    }

    #[test]
    fn fast_crossing_keeps_remainder_inside_adjacent_screen() {
        let mut nav = nav();
        let upper = nav.move_to(p(-100, -110), p(-100, -90));
        assert!((175..=181).contains(&upper.y));
        assert!(contains(nav.clip(), upper));
        assert_eq!(nav.move_to(p(220, 0), p(200, 0)), p(220, 0));
    }

    #[test]
    fn clipped_corner_can_change_direction_without_guessing() {
        let mut nav = nav();
        let corner = nav.move_to(p(-250, -120), p(-190, -90));
        assert_eq!(corner, p(-200, -100));
        assert_eq!(nav.move_to(corner, corner), corner);
        assert_eq!(nav.raw_edge_move(p(-10, 0), corner), corner);
        let upper = nav.raw_edge_move(p(0, -10), corner);
        assert!(contains(r(0, 0, 400, 200), upper));
    }

    #[test]
    fn raw_input_never_adds_movement_inside_a_screen_or_during_drag() {
        let mut nav = nav();
        assert_eq!(nav.raw_edge_move(p(500, -500), p(-100, -50)), p(-100, -50));
        nav.button(1, true);
        assert_eq!(nav.raw_edge_move(p(0, -10), p(-100, -100)), p(-100, -100));
    }

    #[test]
    fn queued_motion_cannot_undo_a_warp_and_clock_wrap_is_supported() {
        let mut nav = nav();
        nav.warped_at(100);
        assert!(!nav.accepts_motion(99));
        assert!(!nav.accepts_motion(100));
        assert!(nav.accepts_motion(101));
        nav.warped_at(u32::MAX);
        assert!(nav.accepts_motion(0));
        assert!(!nav.accepts_motion(u32::MAX - 1));
    }
    #[test]
    fn gaps_and_corner_only_contacts_are_not_neighbours() {
        let mut layout = screens();
        layout[1].logical = r(400, 200, 600, 300);
        let mut nav = Navigation::new(layout, p(399, 198), 0).unwrap();
        assert_eq!(nav.move_to(p(450, 240), p(399, 198)), p(399, 199));
        let mut layout = screens();
        layout[1].logical.top = 201;
        let mut nav = Navigation::new(layout, p(200, 198), 0).unwrap();
        assert_eq!(nav.move_to(p(200, 205), p(200, 198)), p(200, 199));
    }
    #[test]
    fn invalid_or_overlapping_layouts_are_rejected() {
        assert!(Navigation::new(vec![], p(0, 0), 0).is_none());
        let mut layout = screens();
        layout[1].physical = layout[0].physical;
        assert!(Navigation::new(layout, p(0, 0), 0).is_none());
        let layout = vec![Screen {
            logical: r(0, 0, 0, 100),
            physical: r(1, 1, 2, 2),
        }];
        assert!(Navigation::new(layout, p(0, 0), 0).is_none());
    }
    #[test]
    fn tiny_and_extreme_rectangles_do_not_overflow() {
        let layout = vec![Screen {
            logical: r(i32::MIN, 0, i32::MAX, 1),
            physical: r(-1, -1, 0, 0),
        }];
        let nav = Navigation::new(layout, p(0, 0), 0).unwrap();
        assert_eq!(nav.handoff(p(0, 0)), p(-1, -1));
    }
}
