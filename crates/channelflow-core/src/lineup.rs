//! Channel lineups, ported from 1.0.0's schedule model.
//!
//! A lineup is the daily programming for one channel, laid out as 48
//! half-hour slots. A slot can span several consecutive slots (a long film or
//! a continuous feed) and holds weighted candidates the playout pass will
//! pick from. Day-of-week and specific-date **overrides** replace the default
//! roster for that day, exactly as 1.0.0's `Lineup` / `LineupOverride` did.
//!
//! Playout (turning slots into timed programmes, the AI generator, the
//! weather/news "continuous" feeds) is not built yet; this module owns the
//! roster itself — the model, validation, and preview resolution — so the
//! editor and the later playout pass share one document.

use chrono::{Datelike, NaiveDate};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// Half-hour slots per day. 1.0.0 scheduled its days in 30-minute blocks.
pub const SLOTS_PER_DAY: usize = 48;

/// One occupied half-hour slot (or more, when `span_slots > 1`).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LineupSlot {
    /// The half-hour block this slot starts at, 0..=47.
    pub slot_index: i32,
    /// How many consecutive blocks this slot occupies. 1.0.0 let a long
    /// programme span several blocks and compacted back down for storage.
    #[serde(default = "default_span")]
    pub span_slots: i32,
    /// A rerun marker — forward-compatible with playout; the editor shows it.
    #[serde(default)]
    pub is_rerun: bool,
    /// What could play here; the playout pass picks one (weighted).
    #[serde(default)]
    pub candidates: Vec<SlotCandidate>,
}

/// What a slot can play.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SlotCandidate {
    /// Where the candidate comes from: `item` is a media item from the base
    /// catalog (by `match_key`), `text` is free-form (a live title, a note).
    #[serde(default = "default_candidate_kind")]
    pub kind: String,
    /// The catalog item's `match_key` this candidate refers to, for
    /// `kind == "item"`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub match_key: Option<String>,
    /// The candidate title (the catalog item's title for `item`, or text).
    #[serde(default)]
    pub title: String,
    /// Pick weight when a slot lists several candidates. The playout pass
    /// chooses proportionally; 1.0.0 stored the same field.
    #[serde(default = "default_weight")]
    pub weight: i32,
}

/// A day the default roster does not run: one weekday, or one calendar date.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LineupOverride {
    /// Assigned by the server when the override is created.
    #[serde(default = "new_override_id")]
    pub id: Uuid,
    #[serde(default)]
    pub name: String,
    /// `day_of_week` (0=Sunday … 6=Saturday) or `specific_date`.
    #[serde(default = "default_override_kind")]
    pub kind: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub day_of_week: Option<i32>,
    /// `YYYY-MM-DD`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub specific_date: Option<String>,
    #[serde(default)]
    pub slots: Vec<LineupSlot>,
}

fn new_override_id() -> Uuid {
    Uuid::new_v4()
}

/// The whole roster for a channel: its default day plus the overrides.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChannelLineup {
    #[serde(default)]
    pub slots: Vec<LineupSlot>,
    #[serde(default)]
    pub overrides: Vec<LineupOverride>,
}

pub fn default_span() -> i32 {
    1
}
pub fn default_candidate_kind() -> String {
    "text".to_string()
}
pub fn default_weight() -> i32 {
    1
}
pub fn default_override_kind() -> String {
    "day_of_week".to_string()
}

impl Default for ChannelLineup {
    fn default() -> Self {
        Self {
            slots: default_slots(),
            overrides: Vec::new(),
        }
    }
}

/// The empty default day: 48 single half-hour slots, none assigned.
pub fn default_slots() -> Vec<LineupSlot> {
    (0..SLOTS_PER_DAY as i32)
        .map(|slot_index| LineupSlot {
            slot_index,
            span_slots: 1,
            is_rerun: false,
            candidates: Vec::new(),
        })
        .collect()
}

/// Whether the slate is one block covering the whole day (a weather or news
/// feed). 1.0.0 rendered those channels as a single continuous cell.
pub fn is_continuous(slots: &[LineupSlot]) -> bool {
    slots.len() == 1
        && slots[0].slot_index == 0
        && slots[0].span_slots >= SLOTS_PER_DAY as i32
}

/// Normalise and validate a roster. Slots are sorted by start, spans clamped
/// to the day, and overlapping/out-of-range blocks are an error so a hand
/// edit can never produce a schedule the editor cannot render.
pub fn validate_slots(slots: &[LineupSlot]) -> Result<Vec<LineupSlot>, LineupError> {
    let mut sorted = slots.to_vec();
    sorted.sort_by_key(|slot| slot.slot_index);
    let mut cursor = 0;
    let mut cleaned = Vec::with_capacity(sorted.len());
    for slot in sorted {
        if slot.slot_index < 0 || slot.slot_index >= SLOTS_PER_DAY as i32 {
            return Err(LineupError(format!(
                "slot {} is outside the day (0..{})",
                slot.slot_index,
                SLOTS_PER_DAY - 1
            )));
        }
        if slot.slot_index < cursor {
            return Err(LineupError(format!(
                "slot {} overlaps the previous block (ends at {})",
                slot.slot_index, cursor
            )));
        }
        let span = (slot.span_slots.max(1) as i32).min(SLOTS_PER_DAY as i32 - slot.slot_index);
        let mut candidate = slot.clone();
        candidate.span_slots = span;
        for item in candidate.candidates.iter_mut() {
            item.kind = if item.kind == "item" && item.match_key.is_some() {
                "item".to_string()
            } else {
                "text".to_string()
            };
            item.weight = item.weight.max(1);
        }
        cursor = slot.slot_index + span;
        cleaned.push(candidate);
    }
    if cleaned.is_empty() {
        return Ok(default_slots());
    }
    Ok(cleaned)
}

/// Resolve the roster that actually runs on `date`: the matching specific-date
/// override wins, then a weekday override, then the default day. Returns the
/// slots plus which override (if any) supplied them.
pub fn resolve_for_date<'a>(
    lineup: &'a ChannelLineup,
    date: NaiveDate,
) -> (Vec<LineupSlot>, Option<&'a LineupOverride>) {
    let iso = date.format("%Y-%m-%d").to_string();
    if let Some(override_) = lineup
        .overrides
        .iter()
        .find(|o| o.kind == "specific_date" && o.specific_date.as_deref() == Some(iso.as_str()))
    {
        return (validate_slots(&override_.slots).unwrap_or_else(|_| default_slots()), Some(override_));
    }
    if let Some(override_) = lineup.overrides.iter().find(|o| {
        o.kind == "day_of_week"
            && o.day_of_week == Some(date.weekday().num_days_from_sunday() as i32)
    }) {
        return (validate_slots(&override_.slots).unwrap_or_else(|_| default_slots()), Some(override_));
    }
    (validate_slots(&lineup.slots).unwrap_or_else(|_| default_slots()), None)
}

/// The half-hour clock labels the grid shows: 00:00, 00:30, …, 23:30.
pub fn slot_clock(slot_index: i32) -> String {
    let minutes = slot_index.max(0).min(SLOTS_PER_DAY as i32 - 1) as u32 * 30;
    format!("{:02}:{:02}", minutes / 60, minutes % 60)
}

#[derive(Debug)]
pub struct LineupError(pub String);

impl std::fmt::Display for LineupError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for LineupError {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_day_is_forty_eight_empty_half_hours() {
        let slots = default_slots();
        assert_eq!(slots.len(), 48);
        assert_eq!(slots[47].slot_index, 47);
        assert!(slots.iter().all(|s| s.candidates.is_empty()));
    }

    #[test]
    fn spans_and_sorts_are_validated() {
        let bad = vec![
            LineupSlot { slot_index: 5, span_slots: 3, is_rerun: false, candidates: vec![] },
            LineupSlot { slot_index: 6, span_slots: 1, is_rerun: false, candidates: vec![] },
        ];
        assert!(validate_slots(&bad).is_err(), "overlapping blocks are rejected");
        let good = vec![
            LineupSlot { slot_index: 5, span_slots: 3, is_rerun: false, candidates: vec![] },
            LineupSlot { slot_index: 8, span_slots: 1, is_rerun: false, candidates: vec![] },
        ];
        let cleaned = validate_slots(&good).unwrap();
        assert_eq!(cleaned.len(), 2);
        assert_eq!(cleaned[1].slot_index, 8);
    }

    #[test]
    fn a_single_low_span_is_clamped_to_the_day() {
        let slots = vec![LineupSlot {
            slot_index: 46,
            span_slots: 9,
            is_rerun: false,
            candidates: vec![],
        }];
        let cleaned = validate_slots(&slots).unwrap();
        assert_eq!(cleaned[0].span_slots, 2, "46 + 2 fills the 48-slot day");
    }

    #[test]
    fn a_full_day_block_counts_as_continuous() {
        let slots = vec![LineupSlot {
            slot_index: 0,
            span_slots: 48,
            is_rerun: false,
            candidates: vec![],
        }];
        assert!(is_continuous(validate_slots(&slots).unwrap().as_slice()));
    }

    #[test]
    fn specific_date_beats_day_of_week_when_resolving() {
        let mut lineup = ChannelLineup::default();
        lineup.overrides.push(LineupOverride {
            id: Uuid::new_v4(),
            name: "Sunday".to_string(),
            kind: "day_of_week".to_string(),
            day_of_week: Some(0),
            specific_date: None,
            slots: vec![LineupSlot {
                slot_index: 0,
                span_slots: 2,
                is_rerun: false,
                candidates: vec![],
            }],
        });
        let (slots, matched) = resolve_for_date(&lineup, NaiveDate::from_ymd_opt(2026, 10, 11).unwrap());
        assert_eq!(slots.len(), 1, "a Sunday override drives the 11th");
        assert_eq!(matched.unwrap().name, "Sunday");
    }
}