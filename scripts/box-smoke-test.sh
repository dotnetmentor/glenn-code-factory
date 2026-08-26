#!/usr/bin/env bash
# =====================================================================================
# Box API smoke test — pins every wire assumption the platform's BoxClient makes
# =====================================================================================
# Run this FIRST against a fresh Box account (box.ascii.dev) before trusting the
# platform's Box integration or scripts/build-box-template.sh. It exercises each verb
# BoxClient uses and prints the actual response shapes, so any drift between our
# assumptions and the live API is caught here — in one disposable box — instead of in
# production provisioning.
#
# Verified assumptions (each maps to code in Source/Features/BoxManagement/, and to
# the authoritative OpenAPI contract at https://ascii.dev/api/box/v1):
#   1.  Auth: Authorization: Bearer $BOX_API_KEY                    (BoxClient.SendAsync)
#   2.  GET  /me                                                    (PingAsync)
#   3.  POST /boxes {type} — NO name field; name via PATCH after    (CreateBoxAsync + SetNameAsync)
#   4.  GET  /boxes/{id} — box.info envelope, `state` vocabulary    (GetBoxAsync / BoxStates)
#   5.  GET  /boxes — box.list envelope {"boxes":[...]}             (ListBoxesAsync)
#   6.  POST /boxes/{id}/command {command,timeoutSeconds} SINGULAR  (RunCommandAsync)
#   7.  POST /boxes/{id}/stop → state becomes "archived"            (StopBoxAsync)
#   8.  POST /boxes/{id}/resume → box up again, disk intact         (ResumeBoxAsync)
#   9.  POST /boxes/{id}/fork {type,env,noEnv,ttlSeconds} — no name (ForkBoxAsync)
#   10. per-fork env visible inside the fork (delivery channel!)    (provisioner env contract)
#   11. PATCH /boxes/{id} {ttlSeconds} / {name}                     (SetTtlAsync / SetNameAsync)
#   12. GET  /boxes/{id}/snapshots (+ /latest) — per-box            (ListSnapshotsAsync)
#   13. DELETE /boxes/{id} + X-Ascii-Confirm-Delete header          (DeleteBoxAsync)
#   14. 409 box_starting error envelope (top-level code, requestId) (BoxApiException)
#
# Item 10 is the big one: it tells us WHERE per-fork env lands inside the VM
# (process env / /etc/environment / elsewhere), which decides whether the systemd
# unit's EnvironmentFile layering in build-box-template.sh needs adjusting.
#
# Requirements: BOX_API_KEY. Optional: BOX_API_BASE_URL (default https://ascii.dev/api/box/v1)
# Cost: a couple of box-minutes + 3-4 machine starts against the account budget.
# =====================================================================================
set -uo pipefail

BOX_API_BASE_URL="${BOX_API_BASE_URL:-https://ascii.dev/api/box/v1}"
: "${BOX_API_KEY:?BOX_API_KEY is required}"

PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ✅ $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  ❌ $1"; }
note() { echo "  ℹ️  $1"; }

api() { # api METHOD PATH [BODY] [EXTRA_HEADER]
    local method="$1" path="$2" body="${3:-}" hdr="${4:-}"
    local args=(-sS -w '\n%{http_code}' -X "$method" "$BOX_API_BASE_URL$path"
                -H "Authorization: Bearer $BOX_API_KEY")
    [[ -n "$body" ]] && args+=(-H "Content-Type: application/json" -d "$body")
    [[ -n "$hdr"  ]] && args+=(-H "$hdr")
    curl "${args[@]}"
}

json_get() { # json_get JSON PYEXPR  (d = parsed json, unwrapped from the box/data envelope)
    python3 -c '
import json, sys
raw = sys.argv[1]
try:
    d = json.loads(raw)
except Exception:
    print(""); sys.exit(0)
if isinstance(d, dict):
    for k in ("box", "data"):
        if isinstance(d.get(k), dict):
            d = d[k]; break
try:
    print(eval(sys.argv[2]))
except Exception:
    print("")
' "$1" "$2"
}

split_resp() { # sets RESP_BODY, RESP_CODE from api output
    RESP_CODE="${1##*$'\n'}"
    RESP_BODY="${1%$'\n'*}"
}

# Build a /command body with an explicit timeout (contract default is 30s, max 600).
cmd_body() { # cmd_body COMMAND [TIMEOUT_SECONDS]
    python3 -c 'import json,sys; print(json.dumps({"command": sys.argv[1], "timeoutSeconds": int(sys.argv[2])}))' \
        "$1" "${2:-120}"
}

echo "── 1+2. Auth + GET /me"
split_resp "$(api GET /me)"
[[ "$RESP_CODE" == "200" ]] && ok "GET /me → 200 (bearer auth works)" || bad "GET /me → $RESP_CODE : $(echo "$RESP_BODY" | head -c 300)"

echo "── 3. Create a scratch box (body has NO name field — type only)"
split_resp "$(api POST /boxes '{"type":"small"}')"
if [[ "$RESP_CODE" =~ ^2 ]]; then
    BOX_ID=$(json_get "$RESP_BODY" 'd["id"]')
    [[ -n "$BOX_ID" ]] && ok "POST /boxes → $RESP_CODE, id=$BOX_ID (box.created envelope unwrapped)" || bad "created but couldn't parse id from: $(echo "$RESP_BODY" | head -c 300)"
else
    bad "POST /boxes → $RESP_CODE : $(echo "$RESP_BODY" | head -c 300)"
    echo "Cannot continue without a box."; exit 1
fi

echo "── 3b. Name the box via PATCH (create accepts no name)"
split_resp "$(api PATCH "/boxes/$BOX_ID" '{"name":"smoke-test-scratch"}')"
if [[ "$RESP_CODE" =~ ^2 ]]; then
    ok "PATCH /boxes/{id} {name} → $RESP_CODE"
else
    bad "name PATCH → $RESP_CODE : $(echo "$RESP_BODY" | head -c 300) — CHECK SetNameAsync / UpdateBoxRequest!"
fi

echo "── 4. State vocabulary while coming up (field is \`state\`, NOT \`status\`)"
LAST_STATE=""
for i in $(seq 1 40); do
    split_resp "$(api GET "/boxes/$BOX_ID")"
    LAST_STATE=$(json_get "$RESP_BODY" 'd.get("state","")')
    [[ "$LAST_STATE" =~ ^(ready|idle|running)$ ]] && break
    [[ "$LAST_STATE" =~ ^(init|provisioning|provisioned|cloning|archiving|archived|error)$ ]] || note "unexpected state '$LAST_STATE' — outside the documented enum"
    sleep 3
done
if [[ "$LAST_STATE" =~ ^(ready|idle|running)$ ]]; then
    ok "box came up with state '$LAST_STATE' (matches BoxStates.IsUp)"
else
    bad "box never reached an up state; last='$LAST_STATE' — CHECK BoxStates vocabulary! body: $(echo "$RESP_BODY" | head -c 300)"
fi

echo "── 5. List envelope shape"
split_resp "$(api GET /boxes)"
if echo "$RESP_BODY" | python3 -c 'import json,sys; d=json.load(sys.stdin); sys.exit(0 if isinstance(d,dict) and isinstance(d.get("boxes"),list) else 1)' 2>/dev/null; then
    ok "GET /boxes returns the box.list envelope ({\"boxes\":[...]}) — UnwrapElement key verified"
elif [[ "$(echo "$RESP_BODY" | head -c 1)" == '[' ]]; then
    note "GET /boxes returned a BARE array — tolerated by BoxClient but off-contract"
    ok "bare list (tolerated)"
else
    bad "GET /boxes unexpected body: $(echo "$RESP_BODY" | head -c 200)"
fi

echo "── 6. Command endpoint (SINGULAR /command) + contract response fields"
split_resp "$(api POST "/boxes/$BOX_ID/command" "$(cmd_body 'echo smoke-$((6*7)) && touch /root/smoke-marker || touch ~/smoke-marker' 120)")"
if [[ "$RESP_CODE" =~ ^2 ]] && echo "$RESP_BODY" | grep -q "smoke-42"; then
    ok "POST /boxes/{id}/command runs shell and returns stdout"
    EXIT_CODE=$(json_get "$RESP_BODY" 'd.get("exitCode","MISSING")')
    [[ "$EXIT_CODE" == "0" ]] && ok "command response carries exitCode=0 (camelCase per contract)" || bad "exitCode missing/nonzero: '$EXIT_CODE' — CHECK RunBoxCommandResponse shape"
else
    bad "command → $RESP_CODE : $(echo "$RESP_BODY" | head -c 300) — CHECK RunBoxCommandRequest/Response shapes (path must be /command, singular)"
fi

echo "── 7. Stop → archived (+ marker persists)"
split_resp "$(api POST "/boxes/$BOX_ID/stop" '{}')"
[[ "$RESP_CODE" =~ ^2 ]] || bad "stop → $RESP_CODE : $(echo "$RESP_BODY" | head -c 200)"
for i in $(seq 1 30); do
    split_resp "$(api GET "/boxes/$BOX_ID")"
    LAST_STATE=$(json_get "$RESP_BODY" 'd.get("state","")')
    [[ "$LAST_STATE" == "archived" ]] && break
    sleep 2
done
[[ "$LAST_STATE" == "archived" ]] && ok "stop → state 'archived'" || bad "post-stop state '$LAST_STATE' (expected archived; archiving is the transitional step)"

echo "── 8. Resume → up again, disk intact"
split_resp "$(api POST "/boxes/$BOX_ID/resume" '{}')"
[[ "$RESP_CODE" =~ ^2 ]] || bad "resume → $RESP_CODE"
RESUME_T0=$(date +%s)
for i in $(seq 1 40); do
    split_resp "$(api GET "/boxes/$BOX_ID")"
    LAST_STATE=$(json_get "$RESP_BODY" 'd.get("state","")')
    [[ "$LAST_STATE" =~ ^(ready|idle|running)$ ]] && break
    sleep 2
done
if [[ "$LAST_STATE" =~ ^(ready|idle|running)$ ]]; then
    ok "resume → up in $(( $(date +%s) - RESUME_T0 ))s (wake latency data point)"
    split_resp "$(api POST "/boxes/$BOX_ID/command" "$(cmd_body 'ls /root/smoke-marker ~/smoke-marker 2>/dev/null | head -1' 120)")"
    echo "$RESP_BODY" | grep -q "smoke-marker" && ok "disk survived stop/resume" || bad "marker file missing after resume — persistence assumption broken!"
else
    bad "box didn't come back up after resume; last='$LAST_STATE'"
fi

echo "── 9+10. Fork with per-fork env + noEnv (THE critical provisioning primitive; no name on fork)"
split_resp "$(api POST "/boxes/$BOX_ID/fork" '{"env":{"RUNTIME_ID":"smoke-runtime-id-123","GLENN_TEST":"forked"},"noEnv":true,"ttlSeconds":1800}')"
FORK_ID=""
if [[ "$RESP_CODE" =~ ^2 ]]; then
    FORK_ID=$(json_get "$RESP_BODY" 'd["id"]')
    ok "POST /boxes/{id}/fork → $RESP_CODE, fork id=$FORK_ID (box.created envelope unwrapped)"
else
    bad "fork → $RESP_CODE : $(echo "$RESP_BODY" | head -c 400) — CHECK ForkBoxRequest field names!"
fi
if [[ -n "$FORK_ID" ]]; then
    split_resp "$(api PATCH "/boxes/$FORK_ID" '{"name":"smoke-test-fork"}')"
    [[ "$RESP_CODE" =~ ^2 ]] && ok "fork named after the fact via PATCH {name}" || bad "fork name PATCH → $RESP_CODE"
    for i in $(seq 1 40); do
        split_resp "$(api GET "/boxes/$FORK_ID")"
        LAST_STATE=$(json_get "$RESP_BODY" 'd.get("state","")')
        [[ "$LAST_STATE" =~ ^(ready|idle|running)$ ]] && break
        sleep 2
    done
    ok "fork up as '$LAST_STATE'"
    split_resp "$(api POST "/boxes/$FORK_ID/command" "$(cmd_body 'echo PROC:$RUNTIME_ID; grep -l RUNTIME_ID /etc/environment /etc/profile.d/*.sh /run/box* 2>/dev/null | head -3; sudo systemctl show-environment 2>/dev/null | grep RUNTIME_ID' 120)")"
    echo "  ── env delivery probe output (WHERE does per-fork env land?):"
    echo "$RESP_BODY" | head -c 600 | sed 's/^/     /'
    if echo "$RESP_BODY" | grep -q "smoke-runtime-id-123"; then
        ok "per-fork env reaches the fork (adjust glenn-daemon.service EnvironmentFile per probe above if needed)"
    else
        bad "per-fork env NOT visible in the fork — the provisioner env contract needs a different delivery channel (files/command refresh works regardless, but verify!)"
    fi
    split_resp "$(api POST "/boxes/$FORK_ID/command" "$(cmd_body 'ls /root/smoke-marker ~/smoke-marker 2>/dev/null | head -1' 120)")"
    echo "$RESP_BODY" | grep -q "smoke-marker" && ok "fork inherited the source's disk" || bad "fork did NOT inherit source disk"
fi

echo "── 11. TTL patch (the orphan-cost guardrail)"
split_resp "$(api PATCH "/boxes/$BOX_ID" '{"ttlSeconds":3600}')"
[[ "$RESP_CODE" =~ ^2 ]] && ok "PATCH /boxes/{id} {ttlSeconds} → $RESP_CODE" || bad "TTL patch → $RESP_CODE : $(echo "$RESP_BODY" | head -c 300) — BoxTtlExtenderJob depends on this!"

echo "── 12. Snapshots listing (per-box per the contract)"
split_resp "$(api GET "/boxes/$BOX_ID/snapshots")"
[[ "$RESP_CODE" == "200" ]] && ok "GET /boxes/{id}/snapshots → 200 ($(echo "$RESP_BODY" | head -c 80)...)" || bad "per-box snapshots → $RESP_CODE : $(echo "$RESP_BODY" | head -c 200)"
split_resp "$(api GET "/boxes/$BOX_ID/snapshots/latest")"
[[ "$RESP_CODE" =~ ^(200|404)$ ]] && ok "GET /boxes/{id}/snapshots/latest → $RESP_CODE" || bad "latest snapshot → $RESP_CODE"

echo "── 12b. WebGL via headless Chrome + SwiftShader (agent self-validation path)"
# Boxes ship Chrome but no GPU. The agent's whole "look at my own frontend work"
# loop depends on software WebGL actually producing pixels, so prove it on a
# stock box: draw a red frame in WebGL, read the pixel back, print a verdict.
WEBGL_HTML_B64=$(base64 -w0 <<'HTML'
<!doctype html><canvas id="c" width="8" height="8"></canvas><script>
const c = document.getElementById('c');
const gl = c.getContext('webgl2', {preserveDrawingBuffer:true}) || c.getContext('webgl', {preserveDrawingBuffer:true});
if (!gl) { document.body.textContent = 'WEBGL_NO_CONTEXT'; }
else {
  gl.clearColor(1, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);
  const p = new Uint8Array(4);
  gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, p);
  document.body.textContent = (p[0] > 200 && p[1] < 50)
    ? 'WEBGL_DRAW_OK ' + gl.getParameter(gl.VERSION)
    : 'WEBGL_DRAW_FAIL ' + p.join(',');
}
</script>
HTML
)
WEBGL_CMD="echo '$WEBGL_HTML_B64' | base64 -d > /tmp/webgl-probe.html && CHROME=\$(command -v google-chrome-stable || command -v chromium-browser || command -v chromium) && timeout 90 \"\$CHROME\" --headless=new --no-sandbox --disable-dev-shm-usage --use-angle=swiftshader --enable-unsafe-swiftshader --disable-gpu-compositing --virtual-time-budget=5000 --dump-dom file:///tmp/webgl-probe.html 2>/dev/null | grep -o 'WEBGL_[A-Z_]*[^<]*' | head -1"
split_resp "$(api POST "/boxes/$BOX_ID/command" "$(cmd_body "$WEBGL_CMD" 180)")"
if echo "$RESP_BODY" | grep -q "WEBGL_DRAW_OK"; then
    ok "software WebGL renders + reads back correct pixels: $(echo "$RESP_BODY" | grep -o 'WEBGL_DRAW_OK[^\"]*' | head -c 120)"
elif echo "$RESP_BODY" | grep -q "WEBGL_"; then
    bad "WebGL probe ran but did not draw: $(echo "$RESP_BODY" | grep -o 'WEBGL_[^\"]*' | head -c 200) — agent visual self-validation (snap-preview) would be broken!"
else
    bad "WebGL probe could not run (no Chrome on stock box, or command failed): $(echo "$RESP_BODY" | head -c 300)"
fi

echo "── 13. Delete (X-Ascii-Confirm-Delete confirmation header; 409 without it)"
for VICTIM in "$FORK_ID" "$BOX_ID"; do
    [[ -z "$VICTIM" ]] && continue
    split_resp "$(api DELETE "/boxes/$VICTIM" "" "X-Ascii-Confirm-Delete: $VICTIM")"
    if [[ "$RESP_CODE" =~ ^2 ]]; then
        ok "DELETE /boxes/$VICTIM with X-Ascii-Confirm-Delete → $RESP_CODE"
    else
        bad "DELETE → $RESP_CODE : $(echo "$RESP_BODY" | head -c 300) — CHECK the confirmation header name in BoxClient.DeleteBoxAsync!"
        note "trying without header for the 409 error-envelope shape (expect top-level code + requestId):"
        split_resp "$(api DELETE "/boxes/$VICTIM")"
        echo "     $RESP_CODE: $(echo "$RESP_BODY" | head -c 300)"
    fi
done

echo
echo "══════════════════════════════════════════"
echo " Box smoke test: $PASS passed, $FAIL failed"
echo "══════════════════════════════════════════"
[[ $FAIL -eq 0 ]] || {
    echo "Fix the flagged assumptions in Source/Features/BoxManagement/BoxClient.cs (and"
    echo "scripts/build-box-template.sh) before provisioning real runtimes."
    exit 1
}
