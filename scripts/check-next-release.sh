#!/usr/bin/env bash
#
# check-next-release.sh — watch ErsatzTV/next for a new release and raise a PR.
#
#   scripts/check-next-release.sh                    plan only, changes nothing
#   scripts/check-next-release.sh --apply            vendor it, open the PR, close older PRs
#   scripts/check-next-release.sh --apply --no-push  everything except the push and the PR
#
# What it does:
#
#   1. reads vendor/ersatztv-next.lock to see what we have vendored
#   2. finds the newest stable upstream release — drafts, prereleases and
#      non-version tags such as `develop` are ignored
#   3. asks GitHub whether that release is actually *ahead* of our vendored
#      commit. "behind" and "identical" are not updates, so a release cut from
#      an older main never opens a PR; that is the point of comparing rather
#      than blindly following the newest tag
#   4. vendors it on a branch named next-release-<tag> and opens a PR
#   5. closes any still-open next-release PR for an older tag, in favour of
#      the newest one — but only *after* the new PR exists, so a failure part
#      way through leaves the older PR covering us instead of leaving nothing
#
# Open PRs are recognised by their head branch name, so no label or other
# bookkeeping has to survive between runs.
#
# The Docker base is bumped to ersatztv/next:<tag> in the same PR, but only
# when upstream has actually published that image tag. Otherwise the Dockerfile
# is left alone and the PR body says so.
#
# Exit codes:  0  nothing to do, or the plan was printed / carried out
#              1  needs a person — a diverged release, or an open PR for a
#                 release newer than this run can see
#              2  something actually went wrong
#
# Requires gh (authenticated), jq and curl. Must be run from the base branch.

set -Eeuo pipefail
trap 'printf "error: failed near line %s\n" "$LINENO" >&2; exit 2' ERR

NEXT_REPO="${NEXT_REPO:-ErsatzTV/next}"
BASE_BRANCH="${BASE_BRANCH:-2.0.0}"
BRANCH_PREFIX="next-release-"
LOCK="vendor/ersatztv-next.lock"
UPDATE="./scripts/update-next.sh"
UPSTREAM_IMAGE="ersatztv/next"
DOCKERFILE="Dockerfile"

APPLY=0
NO_PUSH=0

usage() {
  cat <<'EOF'
check-next-release.sh — watch ErsatzTV/next for a new release and raise a PR.

  (no flags)        plan only: print what would happen, change nothing
  --apply           vendor the release, open the PR, close older PRs
  --no-push         with --apply: do everything except push and open the PR
  --base <branch>   PR base (default: 2.0.0)
  --next-repo <o/r> upstream repo (default: ErsatzTV/next)
  -h, --help        this text

Exit codes:  0  nothing to do / plan printed / work carried out
             1  needs a person to look at it
             2  error
EOF
}

say()  { printf '%s\n' "$*"; }
info() { printf '  %s\n' "$*"; }
die()  { printf 'error: %s\n' "$*" >&2; exit 2; }

lock_get() {
  [ -f "$LOCK" ] || return 0
  sed -n "s/^$1 = \"\([^\"]*\)\"\$/\1/p" "$LOCK" | head -n1
}

# is_older <tag> <tag> — succeeds when the first tag is the older release.
# sort -V, never plain sort: v0.9.0 would otherwise sort after v0.10.0.
is_older() {
  [ "$1" = "$2" ] && return 1
  local oldest
  oldest=$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n1)
  [ "$oldest" = "$1" ]
}

docker_tag_exists() {
  curl -fsS --max-time 15 \
    "https://hub.docker.com/v2/repositories/$UPSTREAM_IMAGE/tags/$1" >/dev/null 2>&1
}

while [ $# -gt 0 ]; do
  case "$1" in
    --apply)     APPLY=1; shift ;;
    --no-push)   NO_PUSH=1; shift ;;
    --base)      BASE_BRANCH="${2:?--base needs a value}"; shift 2 ;;
    --next-repo) NEXT_REPO="${2:?--next-repo needs a value}"; shift 2 ;;
    -h|--help)   usage; exit 0 ;;
    *)           printf 'unknown option: %s\n\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ "$NO_PUSH" -eq 1 ] && [ "$APPLY" -eq 0 ]; then
  die "--no-push only makes sense with --apply"
fi

command -v gh >/dev/null 2>&1 || die "gh is required"
command -v jq >/dev/null 2>&1 || die "jq is required"
command -v curl >/dev/null 2>&1 || die "curl is required"

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$ROOT"
[ -f "$LOCK" ] || die "$LOCK not found — run from a ChannelFlow checkout"

# ------------------------------------------------------------------ state
old_ref=$(lock_get ref)
old_commit=$(lock_get commit)
if [ -z "$old_commit" ]; then
  die "$LOCK has no commit — run scripts/update-next.sh once first"
fi
old_short=${old_commit:0:7}

if [ "$APPLY" -eq 1 ]; then
  if [ "$NO_PUSH" -eq 1 ]; then mode="apply, no push"; else mode="apply"; fi
else
  mode="plan only"
fi

say "=== ErsatzTV/next release check ==="
info "vendored:   ${old_ref:-?} @ $old_short"
info "base:       $BASE_BRANCH"
info "mode:       $mode"

# ------------------------------------------------- newest stable release
releases=$(gh api "repos/$NEXT_REPO/releases?per_page=100") \
  || die "could not read releases from $NEXT_REPO (is GH_TOKEN set in CI?)"

# jq has no sort -V, so version ordering happens in the shell below.
tags_raw=$(jq -r '
  .[]
  | select(.draft == false)
  | select(.prerelease == false)
  | .tag_name
  | select(test("^v?[0-9]"))
  | select(contains("-") | not)
' <<<"$releases") || die "could not parse the release list"

# grep re-checks for a tag: printf on an empty string still emits a newline,
# which mapfile would otherwise turn into one empty array element.
mapfile -t stable_tags < <(printf '%s\n' "$tags_raw" | grep -E '^v?[0-9]' | sort -V)

if [ ${#stable_tags[@]} -eq 0 ]; then
  info "no stable releases published on $NEXT_REPO — nothing to do"
  exit 0
fi

newest_tag="${stable_tags[${#stable_tags[@]}-1]}"
newest_url=$(jq -r --arg t "$newest_tag" \
  '[.[] | select(.tag_name == $t)][0].html_url // empty' <<<"$releases")
newest_published=$(jq -r --arg t "$newest_tag" \
  '[.[] | select(.tag_name == $t)][0].published_at // empty' <<<"$releases")

info "newest:     $newest_tag  (published ${newest_published:-unknown})"
info "candidates: ${stable_tags[*]}"

# -------------------------------------------------- is it actually newer?
compare=$(gh api "repos/$NEXT_REPO/compare/${old_commit}...${newest_tag}") \
  || die "could not compare $old_short against $newest_tag on $NEXT_REPO"

status=$(jq -r '.status' <<<"$compare")
ahead_by=$(jq -r '.ahead_by // 0' <<<"$compare")
behind_by=$(jq -r '.behind_by // 0' <<<"$compare")

say ""
case "$status" in
  ahead)
    info "compare $old_short...$newest_tag: ahead by $ahead_by commit(s) — there is an update" ;;
  identical)
    info "compare $old_short...$newest_tag: identical — we already have it" ;;
  behind)
    info "compare $old_short...$newest_tag: behind by $behind_by — we are ahead of the release"
    info "our vendored copy tracks ${old_ref:-main}, which has moved past $newest_tag" ;;
  diverged)
    say "  !! $newest_tag has commits we do not, and we have commits it does not."
    say "  !! It was probably cut from something other than ${old_ref:-main}."
    say "  !! Vendoring it would drop content. This needs a person."
    exit 1 ;;
  *)
    say "  !! unexpected compare status '$status' — needs a person."
    exit 1 ;;
esac

# ------------------------------------------------ open PRs, and the plan
prs=$(gh pr list --state open --limit 200 \
  --json number,title,url,headRefName) || die "could not list pull requests"

close_tags=()
close_nums=()
have_newest=no
anomaly=no

while IFS=$'\t' read -r num headref; do
  [ -n "${headref:-}" ] || continue
  case "$headref" in
    "$BRANCH_PREFIX"*) ;;
    *) continue ;;
  esac
  tag=${headref#"$BRANCH_PREFIX"}
  if is_older "$tag" "$newest_tag"; then
    close_tags+=("$tag")
    close_nums+=("$num")
  elif [ "$tag" = "$newest_tag" ]; then
    have_newest=yes
  else
    anomaly=yes
    info "open PR #$num is for $tag, newer than the newest release we can see"
  fi
done < <(jq -r '.[] | "\(.number)\t\(.headRefName)"' <<<"$prs")

create=yes
skip_reason=""
if [ "$anomaly" = yes ]; then
  skip_reason="an open PR targets a release newer than this run can see — release list may be stale"
elif [ "$have_newest" = yes ]; then
  skip_reason="a PR for $newest_tag is already open"
elif [ "$status" != "ahead" ]; then
  skip_reason="$newest_tag is not ahead of our vendored copy"
fi
if [ -n "$skip_reason" ]; then
  create=no
fi

say ""
info "plan:"
if [ "$create" = yes ]; then
  info "  open   ${BRANCH_PREFIX}${newest_tag} -> $BASE_BRANCH"
else
  info "  hold   $skip_reason"
fi
if [ ${#close_nums[@]} -gt 0 ]; then
  for i in "${!close_nums[@]}"; do
    info "  close  #${close_nums[$i]} (${close_tags[$i]}) in favour of $newest_tag"
  done
else
  info "  close  nothing"
fi

# --------------------------------------------------------------- plan only
if [ "$APPLY" -eq 0 ]; then
  say ""
  info "plan only — nothing was changed. Re-run with --apply to act."
  exit 0
fi

# ------------------------------------------------------- carrying it out
needs_attention=0
if [ "$anomaly" = yes ]; then
  needs_attention=1
fi

on_base_branch() {
  if [ "$(git rev-parse --abbrev-ref HEAD)" = "$BASE_BRANCH" ]; then
    return 0
  fi
  git rev-parse --verify -q "refs/remotes/origin/$BASE_BRANCH" >/dev/null || return 1
  [ "$(git rev-parse HEAD)" = "$(git rev-parse "refs/remotes/origin/$BASE_BRANCH")" ]
}

branch="${BRANCH_PREFIX}${newest_tag}"
image_args=()

if [ "$create" = yes ]; then
  say ""
  if ! on_base_branch; then
    die "not on the $BASE_BRANCH branch (on '$(git rev-parse --abbrev-ref HEAD)'). Check out $BASE_BRANCH first."
  fi

  if ! git config user.email >/dev/null 2>&1; then
    git config user.name  "github-actions[bot]"
    git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
  fi

  # Start point is HEAD, so no file changes happen here — safe to do from a
  # script that lives in the working tree.
  git switch -C "$branch" HEAD
  info "on branch $branch"

  if docker_tag_exists "$newest_tag"; then
    image_args=(--image-tag "$newest_tag")
    info "$UPSTREAM_IMAGE:$newest_tag exists — the Docker base will be bumped with it"
  else
    info "$UPSTREAM_IMAGE:$newest_tag is not published yet — the Docker base is left alone"
  fi

  say ""
  if [ ${#image_args[@]} -gt 0 ]; then
    "$UPDATE" --ref "$newest_tag" "${image_args[@]}"
  else
    "$UPDATE" --ref "$newest_tag"
  fi
  say ""

  if [ -z "$(git status --porcelain)" ]; then
    info "$newest_tag produced no file changes — nothing to commit"
    git switch "$BASE_BRANCH" >/dev/null 2>&1 || true
    create=no
  else
    new_commit=$(lock_get commit)
    schema_changed=$(lock_get schema_changed)
    image_tag=$(lock_get image_tag)
    docker_line=$(sed -n 's|^FROM ersatztv/next:\(.*\)$|\1|p' "$DOCKERFILE" 2>/dev/null | head -n1)
    docker_line=${docker_line:-unknown}

    if [ "$schema_changed" = "yes" ]; then
      schema_cell="**changed — check the playout writer before merging**"
      schema_note="
\`vendor/ersatztv-next/schema/\` moved. That is the contract ChannelFlow's
output has to satisfy — read the diff in this PR before merging."
    else
      schema_cell="unchanged"
      schema_note=""
    fi

    if [ "$docker_line" = "$newest_tag" ]; then
      image_note="Upstream has published \`$UPSTREAM_IMAGE:$newest_tag\`, so the base moved with the vendored copy."
    else
      image_note="Upstream has not published \`$UPSTREAM_IMAGE:$newest_tag\` yet, so the base was left at \`$docker_line\`."
    fi

    run_url=""
    run_note=""
    if [ -n "${GITHUB_RUN_ID:-}" ]; then
      run_url="${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}"
      run_note=" [Run]($run_url)"
    fi

    git add -A
    info "changes staged:"
    git diff --cached --stat | sed 's/^/    /'

    title="chore(next): vendor ErsatzTV/next $newest_tag"

    body=$(cat <<EOF
Upstream release: ${newest_url:-$NEXT_REPO/releases}
Published: ${newest_published:-unknown}

| | |
|---|---|
| vendored before | \`${old_ref:-?} @ ${old_short}\` |
| vendored after  | \`${newest_tag} @ ${new_commit:0:7}\` |
| \`schema/\` contract | $schema_cell |
| Docker base | \`ersatztv/next:$docker_line\` |

$image_note
$schema_note
---
Raised by \`scripts/check-next-release.sh\`.$run_note
EOF
)

    git commit -q -m "$title" -m "$body"
    info "committed on $branch"

    if [ "$NO_PUSH" -eq 1 ]; then
      say ""
      info "--no-push: stopping before the push."
      info "would push:  git push origin HEAD:refs/heads/$branch"
      info "would open:  gh pr create --base $BASE_BRANCH --head $branch"
      say "--- PR body that would be used ---"
      printf '%s\n' "$body" | sed 's/^/  /'
      say ""
      info "you are on local branch '$branch'. Switch back with: git switch $BASE_BRANCH"
      exit 0
    fi

    git push --force origin "HEAD:refs/heads/$branch"
    info "pushed $branch"

    if ! pr_url=$(gh pr create --base "$BASE_BRANCH" --head "$branch" \
        --title "$title" --body "$body" 2>&1); then
      die "gh pr create failed: $pr_url"
    fi
    info "opened: $pr_url"
  fi
fi

# --------------------------------------------------- close superseded PRs
if [ ${#close_nums[@]} -gt 0 ]; then
  say ""
  for i in "${!close_nums[@]}"; do
    if gh pr close "${close_nums[$i]}" \
        --comment "Superseded by \`${newest_tag}\` — closing in favour of the newest ErsatzTV/next release." \
        --delete-branch; then
      info "closed #${close_nums[$i]} (${close_tags[$i]})"
    else
      info "warning: could not close #${close_nums[$i]}"
    fi
  done
fi

say ""
if [ "$needs_attention" -eq 1 ]; then
  say "finished, but something above needs a person."
  exit 1
fi
if [ "$create" = yes ]; then
  info "done — PR opened for $newest_tag."
else
  info "done — nothing to open."
fi
