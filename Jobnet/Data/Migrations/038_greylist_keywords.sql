-- 038: Greylist keywords on the candidate profile. When a job's title / summary / description /
-- company name contains a greylist token (whole-word, case-insensitive), it's auto-downvoted
-- (InterestLevel = NotInteresting) — both at discovery time on new jobs and via a sweep
-- triggered when the user saves Profile.

INSERT OR IGNORE INTO config (key, value) VALUES
    ('profile_greylist_keywords', '');
