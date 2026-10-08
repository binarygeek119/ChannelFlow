#!/usr/bin/env bash
#
# update-next.sh — refresh the vendored ErsatzTV/next reference copy.
#
#   scripts/update-next.sh                      vendor to newest `main`, report image status
#   scripts/update-next.sh --ref v0.2.0         vendor to a branch, tag or commit
#   scripts/update-next.sh --image-tag v0.2.0   also rewrite `FROM ersatztv/next:` in the Dockerfile
#   scripts/update-next.sh --check              report only; exit 1 if the vendored copy is stale
#   scripts/update-next.sh --dry-run            do all the work, write nothing
#   scripts/update-next.sh --pull               docker pull the base image after reporting
#
# Writes: vendor/ersatztv-next/, vendor/ersatztv-next.lock, README.md —
# and the Dockerfile only when --image-tag is given.
#
# The part that matters is the schema diff. vendor/ersatztv-next/schema/ holds
# playout.json, channel_config.json and lineup_config.json, which together are
# the contract ChannelFlow's output has to satisfy. If those files move, the
# writer needs a look before anything downstream is trusted, so the script
# prints the diff and records it in the lock file rather than letting an
# upstream change land silently.

set -Eeuo pipefail
trap 'printf "error: failed near line %s\n" "$LINENO" >&2; exit 2' ERR

REPO="https://github.com/ErsatzTV/next.git"
VENDOR="vendor/ersatztv-next"
LOCK="vendor/ersatztv-next.lock"
OLD="vendor/ersatztv-next.old"
README="README.md"
DOCKERFILE="Dockerfile"
DEF_REF="main"

REF="$DEF_REF"
IMAGE_TAG=""
DRY=0
CHECK=0
PULL=0
WORK=""

usage() {
  cat <<'EOF'
update-next.sh — refresh the vendored ErsatzTV/next copy.

  --ref <branch|tag|sha>   what to vendor (default: main)
  --image-tag <tag>        rewrite `FROM ersatztv/next:<tag>` in the Dockerfile
  --check                  report only; exit 1 if the copy is behind, 2 on error
  --dry-run                do everything except write
  --pull                   docker pull the base image afterwards
  -h, --help               this text
EOF
}

info() { printf '  %s\n' "$*"; }
die()  { printf 'error: %s\n' "$*" >&2; exit 2; }

cleanup() {
  if [ -n "${WORK:-}" ]; then rm -rf "$WORK"; fi
  rm -rf "$OLD" 2>/dev/null || true
}
trap cleanup EXIT

lock_get() {
  [ -f "$LOCK" ] || return 0
  sed -n "s/^$1 = \"\([^\"]*\)\"\$/\1/p" "$LOCK" | head -n1
}

# Best-effort resolution of a branch/tag to a commit sha. Used only to decide
# whether a download is worth doing — the clone's own HEAD is authoritative,
# so a mismatch here can never corrupt the vendored copy.
resolve_ref_sha() {
  local out sha
  out=$(git ls-remote "$REPO" "refs/heads/$1" "refs/tags/$1" 2>/dev/null || true)
  [ -n "$out" ] || return 1
  sha=$(printf '%s\n' "$out" | awk '{print $1; exit}')
  printf '%s' "$sha"
}

# A full sha is already resolved and cannot be looked up via ls-remote.
resolve_target() {
  if printf '%s' "$REF" | grep -qE '^[0-9a-f]{40}$'; then
    printf '%s' "$REF"
  else
    resolve_ref_sha "$REF"
  fi
}

# ---------------------------------------------------------------- arguments
while [ $# -gt 0 ]; do
  case "$1" in
    --ref)        REF="${2:?--ref needs a value}"; shift 2 ;;
    --image-tag)  IMAGE_TAG="${2:?--image-tag needs a value}"; shift 2 ;;
    --dry-run)    DRY=1; shift ;;
    --check)      CHECK=1; shift ;;
    --pull)       PULL=1; shift ;;
    -h|--help)    usage; exit 0 ;;
    *)            printf 'unknown option: %s\n\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

command -v git >/dev/null || die "git is required"

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT"

CURRENT=$(lock_get commit)
CURRENT_REF=$(lock_get ref)
CURRENT_SCHEMA=$(lock_get schema_changed)
NOW=$(date -u +%Y-%m-%dT%H:%M:%SZ)

# The base tag the lock should record, before any --image-tag rewrite below.
cur_tag=$(sed -n 's|^FROM ersatztv/next:\([A-Za-z0-9._-]*\).*$|\1|p' "$DOCKERFILE" | head -n1 || true)

# ---------------------------------------------------------------- check mode
if [ "$CHECK" -eq 1 ]; then
  printf '=== check: %s against %s ===\n\n' "$VENDOR" "$REF"
  TARGET=$(resolve_target) || die "could not resolve ref '$REF' (offline?)"
  info "vendored: ${CURRENT:-<no lock file>}  ${CURRENT_REF:+($CURRENT_REF)}"
  info "upstream: ${TARGET}  ($REF)"
  if [ -z "$CURRENT" ]; then
    info "no lock file — run scripts/update-next.sh to initialise"
    exit 1
  fi
  if [ "$CURRENT" = "$TARGET" ]; then
    info "up to date"
    exit 0
  fi
  info "STALE — the vendored copy is behind $REF"
  exit 1
fi

# ---------------------------------------------------------------- resolve
printf '=== vendored copy: %s -> %s ===\n\n' "${CURRENT_REF:-?}${CURRENT:+ @ ${CURRENT:0:7}}" "$REF"
TARGET=$(resolve_target) || die "could not resolve ref '$REF' (branches, tags and full commits are supported)"

if [ -n "$CURRENT" ] && [ "$CURRENT" = "$TARGET" ]; then
  info "already at $REF (${CURRENT:0:7}) — nothing to vendor"
else
  info "target: ${TARGET:0:7}  ($REF)"

  # -------------------------------------------------------------- download
  WORK=$(mktemp -d)
  CLONE="$WORK/clone"
  STAGE_DIR="$WORK/export"
  mkdir -p "$STAGE_DIR"

  if printf '%s' "$REF" | grep -qE '^[0-9a-f]{40}$'; then
    info "fetching commit $REF"
    git init --quiet "$CLONE"
    git -C "$CLONE" remote add origin "$REPO"
    git -C "$CLONE" fetch --quiet --depth 1 origin "$REF"
    git -c advice.detachedHead=false -C "$CLONE" checkout --quiet FETCH_HEAD
  else
    info "cloning $REF (depth 1)"
    git clone --quiet --depth 1 --branch "$REF" "$REPO" "$CLONE"
  fi

  HEAD_SHA=$(git -C "$CLONE" rev-parse HEAD)
  HEAD_DATE=$(git -C "$CLONE" log -1 --format=%cI)
  HEAD_SUBJECT=$(git -C "$CLONE" log -1 --format=%s | tr -d '"\\')

  if [ -n "$CURRENT" ] && [ "$HEAD_SHA" = "$CURRENT" ]; then
    info "resolved to ${HEAD_SHA:0:7}, already vendored — nothing to do"
  else
    info "exporting $(git -C "$CLONE" ls-tree -r --name-only HEAD | wc -l) tracked files"
    git -C "$CLONE" archive HEAD | tar -x -C "$STAGE_DIR"

    [ -f "$STAGE_DIR/schema/playout.json" ] \
      || die "export looks incomplete: schema/playout.json is missing"

    # ---------------------------------------------------------- schema diff
    CHANGED=(); ADDED=(); REMOVED=()
    if [ -d "$STAGE_DIR/schema" ]; then
      for f in "$STAGE_DIR"/schema/*.json; do
        [ -e "$f" ] || continue
        b=$(basename "$f")
        if [ ! -f "$VENDOR/schema/$b" ]; then
          ADDED+=("$b")
        elif ! diff -q "$VENDOR/schema/$b" "$f" >/dev/null 2>&1; then
          CHANGED+=("$b")
        fi
      done
    fi
    if [ -d "$VENDOR/schema" ]; then
      for f in "$VENDOR"/schema/*.json; do
        [ -e "$f" ] || continue
        b=$(basename "$f")
        [ -f "$STAGE_DIR/schema/$b" ] || REMOVED+=("$b")
      done
    fi

    if [ ${#CHANGED[@]} -gt 0 ] || [ ${#ADDED[@]} -gt 0 ] || [ ${#REMOVED[@]} -gt 0 ]; then
      printf '\n'
      printf '  !!! schema/ changed — the playout contract moved !!!\n'
      printf '  !!! re-check ChannelFlow against these files      !!!\n'
      printf '\n'
      for b in ${CHANGED[@]+"${CHANGED[@]}"}; do info "changed: $b"; done
      for b in ${ADDED[@]+"${ADDED[@]}"};   do info "added:   $b";   done
      for b in ${REMOVED[@]+"${REMOVED[@]}"}; do info "removed: $b";  done
      printf '\n'
      for b in ${CHANGED[@]+"${CHANGED[@]}"}; do
        lines=$( (diff -u "$VENDOR/schema/$b" "$STAGE_DIR/schema/$b" || true) | wc -l )
        printf '  --- %s\n' "$b"
        diff -u "$VENDOR/schema/$b" "$STAGE_DIR/schema/$b" | head -n 60 || true
        if [ "$lines" -gt 60 ]; then printf '      ... diff cut off (%s lines total)\n' "$lines"; fi
        printf '\n'
      done
      SCHEMA_MOVED=yes
    else
      SCHEMA_MOVED=no
      info "schema/ unchanged — contract intact"
    fi

    # upstream changelog, when it moved
    if [ -f "$VENDOR/CHANGELOG.md" ] && ! diff -q "$VENDOR/CHANGELOG.md" "$STAGE_DIR/CHANGELOG.md" >/dev/null 2>&1; then
      printf '\n  CHANGELOG.md moved — top of file:\n'
      head -n 15 "$STAGE_DIR/CHANGELOG.md" | sed 's/^/    /'
      printf '\n'
    fi

    # -------------------------------------------------------------- swap
    if [ "$DRY" -eq 1 ]; then
      info "dry run — vendored copy left untouched"
    else
      rm -rf "$OLD"
      [ -d "$VENDOR" ] && mv "$VENDOR" "$OLD"
      mv "$STAGE_DIR" "$VENDOR"
      rm -rf "$OLD"
      info "swapped -> $VENDOR"

      # -------------------------------------------------------- lock file
      cat > "$LOCK" <<EOF
# Written by scripts/update-next.sh. Do not edit by hand.
# Provenance for the vendored copy in vendor/ersatztv-next/.

repo = "$REPO"
ref = "$REF"
commit = "$HEAD_SHA"
commit_date = "$HEAD_DATE"
commit_subject = "$HEAD_SUBJECT"
schema_changed = "$SCHEMA_MOVED"
updated = "$NOW"
image_tag = "${IMAGE_TAG:-$cur_tag}"
EOF
      info "wrote $LOCK"

      # -------------------------------------------------------- README
      if grep -qE 'at `[A-Za-z0-9._/-]+` commit `[0-9a-f]{7,40}`' "$README"; then
        sed -i -E \
          -e "s|at \`[A-Za-z0-9._/-]+\` commit|at \`$REF\` commit|" \
          -e "s|commit \`[0-9a-f]{7,40}\`|commit \`${HEAD_SHA:0:7}\`|" \
          "$README"
        info "updated the commit reference in $README"
      else
        info "warning: could not find the commit sentence in $README"
      fi
    fi
  fi
fi

# ---------------------------------------------------------------- image
printf '\n=== base image ===\n\n'

if [ -n "$IMAGE_TAG" ] && [ "$IMAGE_TAG" != "$cur_tag" ]; then
  if [ "$DRY" -eq 1 ]; then
    info "dry run — would rewrite FROM ersatztv/next:$cur_tag -> :$IMAGE_TAG"
  else
    sed -i -E "s|^FROM ersatztv/next:.*$|FROM ersatztv/next:$IMAGE_TAG|" "$DOCKERFILE"
    cur_tag="$IMAGE_TAG"
    info "rewrote Dockerfile base -> ersatztv/next:$IMAGE_TAG"
  fi
fi

# The lock is only rewritten on a vendor change, so a run that only bumps the
# image tag (hitting the already-vendored fast path above) has to sync this
# field separately or the lock would keep reporting the previous base.
if [ "$DRY" -eq 0 ] && [ -f "$LOCK" ]; then
  desired="${IMAGE_TAG:-$cur_tag}"
  if [ "$(lock_get image_tag)" != "$desired" ]; then
    sed -i "s/^image_tag = \".*\"$/image_tag = \"$desired\"/" "$LOCK"
    info "recorded image_tag = \"$desired\" in $LOCK"
  fi
fi

report_image() {
  info "Dockerfile base: ersatztv/next:${cur_tag:-?}"

  command -v curl >/dev/null || { info "(curl unavailable — skipping Docker Hub lookup)"; return 0; }
  command -v python3 >/dev/null || { info "(python3 unavailable — skipping Docker Hub lookup)"; return 0; }

  local meta
  if meta=$(curl -fsS --max-time 15 "https://hub.docker.com/v2/repositories/ersatztv/next/tags/${cur_tag}" 2>/dev/null); then
    printf '%s' "$meta" | python3 -c '
import sys, json
d = json.load(sys.stdin)
print("  pushed:   %s" % d.get("last_updated", "?")[:19])
print("  digest:   %s" % (d.get("digest") or "?"))
print("  size:     %d MB" % (d.get("full_size", 0) // 1048576))
' || info "(could not parse tag metadata)"
  else
    info "tag '${cur_tag}' not found on Docker Hub"
  fi

  printf '\n  published version tags (a new one means an update is waiting):\n'
  curl -fsS --max-time 15 "https://hub.docker.com/v2/repositories/ersatztv/next/tags?page_size=100" 2>/dev/null \
    | python3 -c '
import sys, json, re
rows = [(t["name"], t["last_updated"][:19]) for t in json.load(sys.stdin).get("results", [])
        if re.fullmatch(r"v\d+\.\d+\.\d+", t["name"])]
for name, when in sorted(rows, key=lambda r: r[1], reverse=True)[:5]:
    print("    %-10s %s" % (name, when))
if not rows:
    print("    (none found)")
' || info "(could not list tags)"
}

report_image || info "(image lookup failed — continuing)"

if [ "$PULL" -eq 1 ]; then
  printf '\n'
  if [ "$DRY" -eq 1 ]; then
    info "dry run — skipping docker pull"
  elif command -v docker >/dev/null; then
    docker pull "ersatztv/next:${cur_tag}" && info "pulled ersatztv/next:${cur_tag}"
  else
    info "docker unavailable — cannot pull"
  fi
fi

# ---------------------------------------------------------------- summary
printf '\n=== summary ===\n\n'
# Read back from the lock so this reflects what was actually written, not the
# state observed before the run.
info "vendored:  $(lock_get ref) @ $(lock_get commit | cut -c1-7)"
info "lock:      $LOCK"
info "schema:    $(lock_get schema_changed)"
info "base:      ersatztv/next:${cur_tag:-?}"
if [ "$DRY" -eq 1 ]; then
  info "mode:      dry run — nothing written"
fi
