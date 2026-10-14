-- Seed for the scratch verify stack (docker-compose.verify.yml, quest 9c8dd002).
-- Run AFTER the app's startup migration has created the schema, then restart the app: the Development
-- startup seed (SeedData.SeedDevelopmentDataAsync) needs one household and one user to exist, and
-- builds the rest (rooms, chores, categories, recipes, extra members) on top of them.
-- Idempotent: each insert runs only on an empty table. Pinned to the schema by VerifyStackSeedTests.
INSERT INTO "Households" ("Name", "CreatedAt")
SELECT 'Verify Household', now()
WHERE NOT EXISTS (SELECT 1 FROM "Households");

INSERT INTO "Users" ("HouseholdId", "Email", "DisplayName", "Initials", "IsWhitelisted", "CreatedAt")
SELECT h."Id", 'verify@dev.local', 'Verify User', 'VU', true, now()
FROM "Households" h
WHERE NOT EXISTS (SELECT 1 FROM "Users")
ORDER BY h."Id"
LIMIT 1;
