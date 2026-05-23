-- 036: Per-candidate preferences that feed into resume-match scoring. The matcher prompt
-- includes these so the AI weights jobs toward what the user actually wants, not just
-- what the resume passively reads as. Empty values are ignored.

INSERT OR IGNORE INTO config (key, value) VALUES
    ('profile_preferred_area_ids',  '[]'),   -- JSON int array, matches areas.id
    ('profile_preferred_level_ids', '[]'),   -- JSON int array, matches levels.id
    ('profile_boost_keywords',      '');     -- comma- or newline-separated free text
