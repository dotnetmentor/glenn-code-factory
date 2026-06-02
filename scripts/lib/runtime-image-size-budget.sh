#!/usr/bin/env bash
# Enforce .image-size.last growth threshold for runtime base image publishes.
# Usage: runtime_image_enforce_size_budget <size_mb> <repo_root>
runtime_image_enforce_size_budget() {
    local size_mb="$1"
    local repo_root="$2"
    local budget_file="${repo_root}/.image-size.last"
    local threshold_pct="${SIZE_GROWTH_THRESHOLD_PCT:-10}"

    if [[ ! -f "$budget_file" ]]; then
        echo "ℹ️  No baseline at $budget_file — establishing one at ${size_mb} MB."
        echo "$size_mb" > "$budget_file"
        return 0
    fi

    local prev_size_mb
    prev_size_mb="$(cat "$budget_file")"
    if [[ -z "$prev_size_mb" || "$prev_size_mb" -le 0 ]]; then
        echo "ℹ️  Baseline invalid; resetting to ${size_mb} MB."
        echo "$size_mb" > "$budget_file"
        return 0
    fi

    local growth_mb growth_pct
    growth_mb=$(( size_mb - prev_size_mb ))
    growth_pct=$(( (growth_mb * 100) / prev_size_mb ))
    echo "   Previous: ${prev_size_mb} MB"
    echo "   Current:  ${size_mb} MB"
    echo "   Growth:   ${growth_mb} MB (${growth_pct}%)"
    if [[ "$growth_pct" -gt "$threshold_pct" ]]; then
        local commit_msg
        commit_msg="$(git -C "$repo_root" log -1 --pretty=%B)"
        if [[ "$commit_msg" == *"[image-size-ok]"* ]]; then
            echo "✅ Override accepted: commit message contains [image-size-ok]."
        else
            echo "❌ Image grew by ${growth_mb} MB (${growth_pct}%, threshold ${threshold_pct}%)." >&2
            echo "   Justify with [image-size-ok] in commit message or shrink." >&2
            return 1
        fi
    fi
    echo "$size_mb" > "$budget_file"
}
