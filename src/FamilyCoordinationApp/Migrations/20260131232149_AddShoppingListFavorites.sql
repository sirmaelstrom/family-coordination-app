-- Migration: Add IsFavorite to ShoppingLists
-- Date: 2026-01-31
-- Purpose: Enable favorites capability for shopping lists

-- Add IsFavorite column
ALTER TABLE "ShoppingLists"
ADD COLUMN "IsFavorite" boolean NOT NULL DEFAULT false;

-- Create index for filtering/sorting by favorites
CREATE INDEX "IX_ShoppingLists_HouseholdId_IsFavorite"
ON "ShoppingLists" ("HouseholdId", "IsFavorite");

-- Rollback SQL (for reference):
-- DROP INDEX "IX_ShoppingLists_HouseholdId_IsFavorite";
-- ALTER TABLE "ShoppingLists" DROP COLUMN "IsFavorite";
