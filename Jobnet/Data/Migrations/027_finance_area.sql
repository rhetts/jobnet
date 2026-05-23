-- 027: Finance as a first-class area + one-time assignment for active jobs whose title
-- contains "Finance" but that aren't already categorised under something else.

INSERT OR IGNORE INTO areas (name, sort_order) VALUES ('Finance', 12);

-- Drop the "Other" tag from finance-titled jobs that have only the Other tag.
-- (After this DELETE they have no area mappings at all.)
DELETE FROM job_areas
WHERE area_id = (SELECT id FROM areas WHERE name = 'Other')
  AND job_id IN (
    SELECT j.id FROM jobs j
    WHERE j.is_active = 1
      AND LOWER(j.title) LIKE '%finance%'
      AND NOT EXISTS (
          SELECT 1 FROM job_areas ja
          WHERE ja.job_id = j.id
            AND ja.area_id <> (SELECT id FROM areas WHERE name = 'Other')
      )
  );

-- Assign Finance to every active job with "Finance" in the title that has no area
-- assignment (covers both the cases above and jobs that were never categorised).
INSERT OR IGNORE INTO job_areas (job_id, area_id)
SELECT j.id, (SELECT id FROM areas WHERE name = 'Finance')
FROM jobs j
WHERE j.is_active = 1
  AND LOWER(j.title) LIKE '%finance%'
  AND NOT EXISTS (SELECT 1 FROM job_areas ja WHERE ja.job_id = j.id);
