ALTER TABLE users
ADD COLUMN allow_tag_search INTEGER NOT NULL DEFAULT 1
CHECK (allow_tag_search IN (0, 1));
