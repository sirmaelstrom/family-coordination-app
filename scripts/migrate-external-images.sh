#!/bin/bash
# One-time migration: Download external recipe images and update database references
# Run inside the familyapp-app container or with access to the database and uploads folder

set -e

UPLOADS_BASE="/app/wwwroot/uploads"
DB_HOST="${DB_HOST:-postgres}"
DB_NAME="${DB_NAME:-familyapp}"
DB_USER="${DB_USER:-familyapp}"

echo "=== External Image Migration ==="
echo "This script downloads external recipe images and updates database references."
echo ""

# Get recipes with external images
RECIPES=$(psql -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" -t -A -c "
    SELECT \"HouseholdId\", \"RecipeId\", \"ImagePath\" 
    FROM \"Recipes\" 
    WHERE \"ImagePath\" LIKE 'http%' 
    AND \"IsDeleted\" = false;
")

if [ -z "$RECIPES" ]; then
    echo "No external images found. Nothing to migrate."
    exit 0
fi

COUNT=$(echo "$RECIPES" | wc -l)
echo "Found $COUNT recipes with external images."
echo ""

MIGRATED=0
FAILED=0

while IFS='|' read -r HOUSEHOLD_ID RECIPE_ID IMAGE_URL; do
    [ -z "$HOUSEHOLD_ID" ] && continue
    
    echo "Processing Recipe $RECIPE_ID (Household $HOUSEHOLD_ID)..."
    echo "  URL: $IMAGE_URL"
    
    # Create uploads directory if needed
    UPLOAD_DIR="$UPLOADS_BASE/$HOUSEHOLD_ID"
    mkdir -p "$UPLOAD_DIR"
    
    # Extract extension from URL (default to .jpg)
    EXT=$(echo "$IMAGE_URL" | grep -oE '\.(jpg|jpeg|png|gif|webp)' | head -1)
    [ -z "$EXT" ] && EXT=".jpg"
    
    # Generate unique filename
    FILENAME="$(uuidgen)$EXT"
    FILEPATH="$UPLOAD_DIR/$FILENAME"
    LOCAL_PATH="/uploads/$HOUSEHOLD_ID/$FILENAME"
    
    # Download image
    if curl -sL -o "$FILEPATH" --max-time 30 "$IMAGE_URL"; then
        # Verify it's actually an image (check magic bytes)
        FILE_TYPE=$(file -b --mime-type "$FILEPATH" 2>/dev/null || echo "unknown")
        if [[ "$FILE_TYPE" == image/* ]]; then
            # Update database
            psql -h "$DB_HOST" -U "$DB_USER" -d "$DB_NAME" -c "
                UPDATE \"Recipes\" 
                SET \"ImagePath\" = '$LOCAL_PATH' 
                WHERE \"HouseholdId\" = $HOUSEHOLD_ID AND \"RecipeId\" = $RECIPE_ID;
            " > /dev/null
            
            echo "  ✅ Saved as $LOCAL_PATH"
            ((MIGRATED++))
        else
            echo "  ❌ Downloaded file is not an image ($FILE_TYPE)"
            rm -f "$FILEPATH"
            ((FAILED++))
        fi
    else
        echo "  ❌ Failed to download"
        ((FAILED++))
    fi
    
done <<< "$RECIPES"

echo ""
echo "=== Migration Complete ==="
echo "Migrated: $MIGRATED"
echo "Failed: $FAILED"
