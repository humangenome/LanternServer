# Lantern.Persistence

SQLite-backed persistence (LanternDb): bans, scheduler, audit, character store,
save-snapshot bookkeeping.

PORT FROM BEACON: copy `Beacon.Persistence` (SQLite/Dapper, the BeaconDb schema)
and rename. Schema ports verbatim; add Grounded-2-specific tables only as the
feature set diverges.
