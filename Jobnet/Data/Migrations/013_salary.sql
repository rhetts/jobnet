-- Salary range (min/max + currency + period). Populated from ATS APIs (Lever, Ashby compensation),
-- JSON-LD baseSalary, or AI extraction. All nullable — most jobs don't disclose.

ALTER TABLE jobs ADD COLUMN salary_min      INTEGER;
ALTER TABLE jobs ADD COLUMN salary_max      INTEGER;
ALTER TABLE jobs ADD COLUMN salary_currency TEXT;   -- 'USD', 'CAD', 'EUR', ...
ALTER TABLE jobs ADD COLUMN salary_period   TEXT;   -- 'year' | 'month' | 'hour' | NULL
