#!/usr/bin/env bash
# Пересобирает .patch из текущего состояния вендорнутого пакета.
#
# Зовётся после каждой правки форка: файл в этой папке — единственное, что переживает бамп
# пакета, и разъехаться с рабочей копией ему нельзя.
#
# Bash, а не PowerShell, намеренно: дифф с русскими комментариями надо записать байт в байт, а
# PowerShell перекодирует поток и съедает кириллицу.
#
# Эталон (коммит с ЧИСТЫМ пакетом) берётся из шапки существующего патча; на первый раз или при
# смене версии передаётся вторым аргументом.
#
#   ./regen.sh <путь к Unity-проекту> [эталонный-коммит]

set -euo pipefail

PROJECT="${1:?первый аргумент — путь к Unity-проекту}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cd "$PROJECT"

PKG_DIR="Packages/com.unity.entities"
[ -d "$PKG_DIR" ] || { echo "нет '$PKG_DIR' — пакет не вендорнут" >&2; exit 1; }

VERSION=$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$PKG_DIR/package.json" | head -1)
OUT="$HERE/com.unity.entities@$VERSION.patch"

BASE="${2:-}"
if [ -z "$BASE" ] && [ -f "$OUT" ]; then
    BASE=$(sed -n 's/^# Эталон: .* в коммите \([0-9a-f]\{7,\}\)\.$/\1/p' "$OUT" | head -1)
fi
if [ -z "$BASE" ]; then
    echo "не из чего вывести эталон: передай вторым аргументом коммит с чистым пакетом" >&2
    exit 1
fi

git cat-file -e "$BASE^{commit}" 2>/dev/null || { echo "коммита '$BASE' в этом репозитории нет" >&2; exit 1; }

# Новые файлы форка в дифф попадают только помеченными к добавлению; содержимое в индекс не уходит.
UNTRACKED=$(git ls-files --others --exclude-standard -- "$PKG_DIR")
if [ -n "$UNTRACKED" ]; then
    echo "$UNTRACKED" | xargs -d '\n' git add -N --
fi

{
    echo "# Форк com.unity.entities $VERSION под патч ссылок Blobcheg."
    echo "#"
    echo "# Эталон: чистый $VERSION, как он лежит в коммите $BASE."
    echo "# Накатывать из корня Unity-проекта, пакет должен быть уже вендорнут в $PKG_DIR:"
    echo "#     git apply --3way Packages/Blobcheg/tools~/entities-patch/com.unity.entities@$VERSION.patch"
    echo "# Проверить, не накатывая:  git apply --check <тот же путь>"
    echo "#"
    echo "# Пересобрать после правки форка: tools~/entities-patch/regen.sh <проект>"
    echo ""
    git diff "$BASE" -- "$PKG_DIR"
} > "$OUT"

if [ -n "$UNTRACKED" ]; then
    echo "$UNTRACKED" | xargs -d '\n' git reset -q --
fi

echo "собран $OUT"
grep '^diff --git' "$OUT" | sed 's/^diff --git a\///; s/ b\/.*//' | sed 's/^/  /'
