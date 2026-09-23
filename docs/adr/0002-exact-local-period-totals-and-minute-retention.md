# ADR 0002 — Exact local-period totals and minute retention

Status: Accepted
Date: 2026-09-22
Amends: ADR 0001 / FILEMCP_OBSERVABILITY_V1_FROZEN retention/query rules

## Problem discovered during OBS-004
The original freeze retained minute rows for only 48 hours and planned 7d/30d totals from UTC hour buckets. This is not exact for local-day boundaries when UTC offset is not a whole hour (for example +05:30 or +05:45) and can also create edge ambiguity around timezone transitions.

## Options reviewed

1. Keep 48h minute retention and accept hour-edge approximation.
Rejected: violates truthful/exact period totals.

2. Maintain timezone-specific hour/day rollups.
Rejected: multiplies data models, breaks when user timezone changes, adds migration complexity.

3. Persist raw events and recompute periods.
Rejected: unnecessary volume/privacy risk; conflicts with content-minimal rollup design.

4. Retain UTC minute rollups long enough for every supported short period.
Selected: simple, timezone-independent storage; exact period totals by UTC bounds derived from local dates.

## Decision
- `usage_minute` retention changes from 48 hours to 35 days.
- Today / Yesterday / 7d / 30d card totals query minute rows for exact local boundaries.
- `usage_hour` remains 90 days and is used for lower-cost chart series / longer custom ranges.
- `usage_day` remains long-term.
- UI can render 7d/30d graphs from hour rows while headline totals come from minute rows.

## Cost
At four active workspaces, 35 days of one row per minute per workspace is about 201,600 maximum aggregate rows before indexes, which is small for SQLite and materially safer than presenting approximate totals.

## New acceptance requirement
Period tests must include a half-hour or quarter-hour timezone plus a DST timezone and prove local start/end boundaries select the exact expected minute buckets.