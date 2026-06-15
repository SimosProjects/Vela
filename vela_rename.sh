#!/usr/bin/env bash
# vela_rename.sh — Renames Vela → Vela throughout the repository.
#
# Usage:
#   ./vela_rename.sh                         # dry run — preview all changes
#   ./vela_rename.sh --apply                 # apply file and content changes
#   ./vela_rename.sh --apply --db            # also rename the PostgreSQL database
#   ./vela_rename.sh --apply --db --db-user  # also rename the DB user
#
# Run from the repository root on a clean git branch.
# Requires perl (standard on macOS and Linux).

set -euo pipefail

APPLY=false
RENAME_DB=false
RENAME_DB_USER=false

for arg in "$@"; do
  case "$arg" in
    --apply)   APPLY=true ;;
    --db)      RENAME_DB=true ;;
    --db-user) RENAME_DB_USER=true ;;
    --help)
      sed -n '2,10p' "$0"
      exit 0 ;;
  esac
done

OLD_PASCAL="Vela"
NEW_PASCAL="Vela"
OLD_LOWER="vela"
NEW_LOWER="vela"
OLD_UPPER="VELA"
NEW_UPPER="VELA"

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$ROOT"

FILES_CHANGED=0
FILES_RENAMED=0
DIRS_RENAMED=0

echo ""
echo "Vela → Vela rename"
echo "========================"
if [[ "$APPLY" == "true" ]]; then
  echo "Mode: APPLY"
else
  echo "Mode: DRY RUN — pass --apply to make changes"
fi
echo ""

# ── Step 1: Update file contents ─────────────────────────────────────────────

echo "Step 1: Updating file contents..."

while IFS= read -r -d '' file; do
  if grep -qE "${OLD_PASCAL}|${OLD_LOWER}|${OLD_UPPER}" "$file" 2>/dev/null; then
    echo "  update  $file"
    if [[ "$APPLY" == "true" ]]; then
      perl -pi -e \
        "s/${OLD_PASCAL}/${NEW_PASCAL}/g; s/${OLD_LOWER}/${NEW_LOWER}/g; s/${OLD_UPPER}/${NEW_UPPER}/g" \
        "$file"
    fi
    FILES_CHANGED=$((FILES_CHANGED + 1))
  fi
done < <(find . \
  -not -path './.git/*' \
  -not -path '*/bin/*' \
  -not -path '*/obj/*' \
  -not -path '*/.venv/*' \
  -not -path '*/node_modules/*' \
  -type f \
  \( \
    -name "*.cs"      -o -name "*.csproj"  -o -name "*.sln"   -o \
    -name "*.json"    -o -name "*.yml"     -o -name "*.yaml"  -o \
    -name "*.md"      -o -name "*.sh"      -o -name "*.txt"   -o \
    -name "*.sql"     -o -name "*.py"      -o -name "*.jsx"   -o \
    -name "*.tsx"     -o -name "*.ts"      -o -name "*.html"  -o \
    -name "*.css"     -o -name "*.props"   -o -name "*.targets" -o \
    -name "Dockerfile" -o -name "*.Dockerfile" -o \
    -name ".env"      -o -name ".env.*"    -o -name "*.env"   \
  \) \
  -print0)

echo "  → ${FILES_CHANGED} file(s) updated"
echo ""

# ── Step 2: Rename files ──────────────────────────────────────────────────────

echo "Step 2: Renaming files..."

while IFS= read -r -d '' file; do
  base="$(basename "$file")"
  newbase="${base//${OLD_PASCAL}/${NEW_PASCAL}}"
  newbase="${newbase//${OLD_LOWER}/${NEW_LOWER}}"
  if [[ "$base" != "$newbase" ]]; then
    dir="$(dirname "$file")"
    echo "  rename  $file → $dir/$newbase"
    if [[ "$APPLY" == "true" ]]; then
      mv "$file" "$dir/$newbase"
    fi
    FILES_RENAMED=$((FILES_RENAMED + 1))
  fi
done < <(find . \
  -not -path './.git/*' \
  -not -path '*/bin/*' \
  -not -path '*/obj/*' \
  -type f \
  -print0)

echo "  → ${FILES_RENAMED} file(s) renamed"
echo ""

# ── Step 3: Rename directories (deepest first) ────────────────────────────────

echo "Step 3: Renaming directories..."

# -depth ensures child directories are processed before their parents
# so renaming Vela.Worker/Migrations before Vela.Worker itself
while IFS= read -r -d '' dir; do
  base="$(basename "$dir")"
  newbase="${base//${OLD_PASCAL}/${NEW_PASCAL}}"
  newbase="${newbase//${OLD_LOWER}/${NEW_LOWER}}"
  if [[ "$base" != "$newbase" ]]; then
    parent="$(dirname "$dir")"
    echo "  rename  $dir/ → $parent/$newbase/"
    if [[ "$APPLY" == "true" ]]; then
      mv "$dir" "$parent/$newbase"
    fi
    DIRS_RENAMED=$((DIRS_RENAMED + 1))
  fi
done < <(find . \
  -not -path './.git/*' \
  -not -path '*/bin/*' \
  -not -path '*/obj/*' \
  -depth \
  -mindepth 1 \
  -type d \
  -print0)

echo "  → ${DIRS_RENAMED} director(ies) renamed"
echo ""

# ── Step 4: PostgreSQL rename (optional) ─────────────────────────────────────

if [[ "$RENAME_DB" == "true" ]]; then
  echo "Step 4: Renaming PostgreSQL database..."
  SQL_DB="ALTER DATABASE ${OLD_LOWER} RENAME TO ${NEW_LOWER};"
  if [[ "$APPLY" == "true" ]]; then
    # Must connect to a different database (postgres) to rename the target
    psql -U postgres -c "$SQL_DB" \
      && echo "  → database ${OLD_LOWER} → ${NEW_LOWER}" \
      || echo "  ✗ failed — you may need to run as the postgres superuser"
  else
    echo "  (dry run) psql -U postgres -c \"${SQL_DB}\""
    echo "  Note: must connect to 'postgres' DB to rename '${OLD_LOWER}'"
  fi

  if [[ "$RENAME_DB_USER" == "true" ]]; then
    SQL_USER="ALTER USER ${OLD_LOWER}_user RENAME TO ${NEW_LOWER}_user;"
    if [[ "$APPLY" == "true" ]]; then
      psql -U postgres -c "$SQL_USER" \
        && echo "  → user ${OLD_LOWER}_user → ${NEW_LOWER}_user" \
        || echo "  ✗ user rename failed"
    else
      echo "  (dry run) psql -U postgres -c \"${SQL_USER}\""
    fi
  fi
  echo ""
fi

# ── Summary ───────────────────────────────────────────────────────────────────

echo "Summary"
echo "-------"
echo "  Content updated : ${FILES_CHANGED} file(s)"
echo "  Files renamed   : ${FILES_RENAMED}"
echo "  Dirs renamed    : ${DIRS_RENAMED}"

if [[ "$APPLY" == "false" ]]; then
  echo ""
  echo "No changes made. To apply:"
  echo "  git checkout -b rename/vela"
  echo "  chmod +x vela_rename.sh"
  echo "  ./vela_rename.sh --apply               # code, configs, filenames"
  echo "  ./vela_rename.sh --apply --db          # + rename database"
  echo "  ./vela_rename.sh --apply --db --db-user  # + rename DB user"
fi

if [[ "$APPLY" == "true" ]]; then
  echo ""
  echo "Next steps:"
  echo "  dotnet build Vela.sln          # confirm solution compiles"
  echo "  dotnet test                    # confirm all tests pass"
  echo "  grep -r Vela . --include='*.cs' --include='*.json'  # catch any stragglers"
  echo "  git add -A && git commit -m 'chore: rename Vela → Vela'"
fi