#!/usr/bin/env bash
# Deploys one Moonlighter Assembly-CSharp.dll variant to the Nova over root adb. Never run from CI; the device is shared.
#   tools/deploy.sh shipped|doors|doors-nosteam|doors-probe|shipped-v1|nosteam-v1|pristine   (shipped == doors, the signed-off build)
#   tools/deploy.sh --rollback        restore the pristine dll from Assembly-CSharp.dll.bak and re-hash
#   tools/deploy.sh --restore-saves logs/backups/<ts>   put a save backup back exactly (the replaced dirs go to /data/local/tmp/ml-pre-restore-<ts>/)
#   tools/deploy.sh --dry-run <variant|--rollback>   print the plan, touch nothing (no adb call at all)
# Procedure follows the nova-43aspect skill: device lock (with the || rmdir idiom), stage in /data/local/tmp then
# root-copy, sha256 on both ends, owner/mode preserved as found, .bak never written, lock always released.
set -euo pipefail

SER=${ADB_SERIAL:-$(adb devices 2>/dev/null | awk 'NR>1 && $2=="device"{print $1; exit}')}   # ADB_SERIAL=<serial> if more than one device is attached
cd "$(dirname "$0")/.."

# known builds (CLAUDE.local.md "Hashes"). a live dll that is not one of these aborts the deploy.
H_PRISTINE=d834f0ae78eab04e2d741b30e352d515f1814aa2330c8ed684d7c59027e55cdc
H_SHIPPED=45e6f2ca04c30ecce0975bde3360a72850ed4d0ee2c0dd2392d53b24ad26fedc
H_NOSTEAM=ed10f442689a5bb35948850e3344a2ef4e7fc8336743b47926abf3d8cab26f95
H_PROBE=861a6730b2a15940b0120702d7774124d71131543a29fc0aa8f687c5006cbee7      # old probe build, no longer in out/ (known so a live copy is recognised)
H_SHIPPED_V1=45e6f2ca04c30ecce0975bde3360a72850ed4d0ee2c0dd2392d53b24ad26fedc  # boot fix only (patcher --no-doorcam)
H_NOSTEAM_V1=ed10f442689a5bb35948850e3344a2ef4e7fc8336743b47926abf3d8cab26f95  # --no-doorcam --no-steam
H_DOORS=c9c25d1de54e87d81641d435d7d9ea116bdb42ed9ccc098b5bb9b61cb20e17b0       # boot fix + dungeon door camera (default patch)
H_DOORS_NOSTEAM=07bad803514ba034da1d23e328145673ee0e6435f7d12e469235a94fe5dbf7db
H_DOORS_PROBE=8b06dfa18725c9163861327c91bc20ddedc2a8e9c8f4556b90de174ab8c2b958

LINUXFS=/data/data/com.droiddeck.launcher/files/linuxfs
SDSTEAM=/mnt/media_rw/6988-E33F/Android/data/com.droiddeck.launcher/files/steam   # always the media_rw path from root adb
LOCK=/data/local/tmp/nova-43aspect.lock
CLOCK=$LINUXFS/tmp/nova-43aspect.lock    # the in-container /tmp lock, taken too when the container's /tmp is there
STAGE=/data/local/tmp/ml_stage.dll

DRY=0
[ "${1:-}" = "--dry-run" ] && { DRY=1; shift; }
MODE=${1:-}
case "$MODE" in
  shipped)  SRC=out/doors/Assembly-CSharp.dll;    WANT=$H_DOORS ;;   # shipped since 2026-11-06 (user sign-off)
  shipped-v1) SRC=out/shipped-v1/Assembly-CSharp.dll; WANT=$H_SHIPPED_V1 ;;
  nosteam-v1) SRC=out/nosteam-v1/Assembly-CSharp.dll; WANT=$H_NOSTEAM_V1 ;;
  doors)    SRC=out/doors/Assembly-CSharp.dll;    WANT=$H_DOORS ;;
  doors-nosteam) SRC=out/doors-nosteam/Assembly-CSharp.dll; WANT=$H_DOORS_NOSTEAM ;;
  doors-probe)   SRC=out/doors-probe/Assembly-CSharp.dll;   WANT=$H_DOORS_PROBE ;;
  --restore-saves) SRC=""; WANT=""; RBK=${2:-}; { [ -d "$RBK" ] && ls "$RBK"/save*.tar >/dev/null 2>&1; } || { echo "[!] --restore-saves needs a backup dir with save*.tar (logs/backups/<ts>)" >&2; exit 2; } ;;
  pristine) SRC=original/Assembly-CSharp.dll;     WANT=$H_PRISTINE ;;
  --rollback) SRC=""; WANT=$H_PRISTINE ;;
  *) sed -n '2,6p' "$0" >&2; exit 2 ;;
esac

sha_local() { shasum -a 256 "$1" | cut -d' ' -f1; }
rsh() { adb -s "$SER" shell su | tr -d '\r'; }      # root shell; commands come in on stdin (heredoc), so paths with spaces survive

if [ -n "$SRC" ]; then
  [ -f "$SRC" ] || { echo "[!] $SRC missing. build it first (dotnet run --project patcher ...)" >&2; exit 1; }
  GOT=$(sha_local "$SRC")
  [ "$GOT" = "$WANT" ] || { echo "[!] $SRC is $GOT, expected $WANT. Refusing to push an unknown build." >&2; exit 1; }
fi

if [ "$DRY" = 1 ]; then
  cat <<EOF
[dry-run] would: take lock $LOCK (and $CLOCK if present) as moonlighter-43aspect
          refuse if gamescope/wineserver is running
          locate the Moonlighter game dir under $SDSTEAM/steamapps/common, then $LINUXFS/root/.local/share/Steam/steamapps/common
          check live dll hash is one of: pristine/shipped/nosteam/probe, and Assembly-CSharp.dll.bak == pristine ($H_PRISTINE)
          back up saves (LocalLow/11BitStudios/Moonlighter + Steam userdata/*/606150) to logs/backups/<timestamp>/ on this Mac
          $([ -n "$SRC" ] && echo "push $SRC ($WANT) via $STAGE" || echo "restore the dll from .bak ($WANT)"), keep owner/mode, verify sha256 both ends
          never write Assembly-CSharp.dll.bak; release lock
EOF
  exit 0
fi

command -v adb >/dev/null || { echo "[!] adb not found" >&2; exit 1; }
adb -s "$SER" get-state >/dev/null 2>&1 || { echo "[!] device $SER not reachable over adb" >&2; exit 1; }

HAVE_LOCK=0; HAVE_CLOCK=0
release() {
  if [ "$HAVE_LOCK" = 1 ] || [ "$HAVE_CLOCK" = 1 ]; then
    { [ "$HAVE_LOCK" = 1 ] && echo "rm -rf $LOCK"; [ "$HAVE_CLOCK" = 1 ] && echo "rm -rf $CLOCK"; true; } | rsh >/dev/null || echo "[!] lock release failed; remove $LOCK by hand" >&2
    HAVE_LOCK=0; HAVE_CLOCK=0
  fi
}
trap release EXIT

# --- lock. the || rmdir is not optional: a failed owner write must not leave an owner-less lock directory.
OUT=$(rsh <<EOF
mkdir $LOCK 2>/dev/null && { echo "moonlighter-43aspect \$(date +%s)" > $LOCK/owner || rmdir $LOCK; }
if [ -f $LOCK/owner ]; then echo "OWNER: \$(cat $LOCK/owner)"; else echo "NOLOCK"; fi
EOF
)
if echo "$OUT" | grep -q '^OWNER: moonlighter-43aspect'; then
  # could be ours (just taken) or an older run of ours. only a fresh take counts; compare the timestamp to now.
  TS=$(echo "$OUT" | awk '{print $3}'); NOW=$(date +%s)
  if [ $((NOW - TS)) -le 120 ]; then HAVE_LOCK=1; fi
fi
if [ "$HAVE_LOCK" != 1 ]; then
  echo "[!] device lock is held (or could not be taken): $OUT" >&2
  echo "    owner file format is '<repo> <epoch>'. Only remove it if older than 45 min AND no game processes run (rmdir if there is no owner file)." >&2
  exit 3
fi
OUT=$(rsh <<EOF
if [ -d $LINUXFS/tmp ]; then mkdir $CLOCK 2>/dev/null && { echo "moonlighter-43aspect \$(date +%s)" > $CLOCK/owner || rmdir $CLOCK; }; [ -f $CLOCK/owner ] && echo CLOCK_OK || echo CLOCK_BUSY; else echo CLOCK_NA; fi
EOF
)
case "$OUT" in *CLOCK_OK*) HAVE_CLOCK=1 ;; *CLOCK_BUSY*) echo "[!] in-container lock $CLOCK is held by someone else" >&2; exit 3 ;; esac
echo "[+] lock held: $LOCK$([ $HAVE_CLOCK = 1 ] && echo " + $CLOCK")"

# --- nobody playing?
RUN=$(rsh <<'EOF'
ps -A -o ARGS 2>/dev/null | grep -E 'gamescope|wineserver' | grep -v grep | head -3
EOF
)
[ -z "$RUN" ] || { echo "[!] a game appears to be running; the user may be playing. Aborting (lock released):" >&2; echo "$RUN" >&2; exit 4; }

# --- find the game and capture the live state
INFO=$(rsh <<EOF
for G in "$SDSTEAM/steamapps/common/Moonlighter" "$LINUXFS/root/.local/share/Steam/steamapps/common/Moonlighter"; do
  if [ -f "\$G/Moonlighter_Data/Managed/Assembly-CSharp.dll" ]; then
    M="\$G/Moonlighter_Data/Managed"
    echo "GAMEDIR=\$G"
    echo "LIVE_SHA=\$(sha256sum "\$M/Assembly-CSharp.dll" | cut -d' ' -f1)"
    echo "BAK_SHA=\$(sha256sum "\$M/Assembly-CSharp.dll.bak" 2>/dev/null | cut -d' ' -f1)"
    echo "LIVE_META=\$(stat -c '%u:%g %a' "\$M/Assembly-CSharp.dll")"
    ls -l "\$M/Assembly-CSharp.dll" "\$M/Assembly-CSharp.dll.bak" 2>/dev/null | sed 's/^/LS: /'
    break
  fi
done
EOF
)
echo "$INFO"
GAMEDIR=$(echo "$INFO" | sed -n 's/^GAMEDIR=//p')
LIVE_SHA=$(echo "$INFO" | sed -n 's/^LIVE_SHA=//p'); BAK_SHA=$(echo "$INFO" | sed -n 's/^BAK_SHA=//p')
META=$(echo "$INFO" | sed -n 's/^LIVE_META=//p')
[ -n "$GAMEDIR" ] || { echo "[!] Moonlighter game dir not found on SD or internal Steam library" >&2; exit 1; }
DLL="$GAMEDIR/Moonlighter_Data/Managed/Assembly-CSharp.dll"
case "$LIVE_SHA" in "$H_PRISTINE"|"$H_SHIPPED"|"$H_NOSTEAM"|"$H_PROBE"|"$H_DOORS"|"$H_DOORS_NOSTEAM"|"$H_DOORS_PROBE") ;; *) echo "[!] live dll $LIVE_SHA is not a known build (someone changed it?). Not touching it." >&2; exit 5 ;; esac
[ "$BAK_SHA" = "$H_PRISTINE" ] || { echo "[!] Assembly-CSharp.dll.bak is '$BAK_SHA', not pristine $H_PRISTINE. Not touching anything." >&2; exit 5; }
UIDGID=${META% *}; PERM=${META#* }
[ -n "$UIDGID" ] && [ -n "$PERM" ] || { echo "[!] could not read owner/mode of the live dll" >&2; exit 5; }

# --- back up saves (read-only on the device; pulled to the Mac)
TS=$(date +%Y%m%d-%H%M%S); BK=logs/backups/$TS; mkdir -p "$BK"
SAVES=$(rsh <<EOF
for P in "$SDSTEAM/steamapps/compatdata/606150/pfx/drive_c/users/steamuser/AppData/LocalLow/11BitStudios/Moonlighter" \
         "$LINUXFS/root/.local/share/Steam/steamapps/compatdata/606150/pfx/drive_c/users/steamuser/AppData/LocalLow/11BitStudios/Moonlighter" \
         /data/data/com.droiddeck.launcher/files/steam-libraries/*/steamapps/compatdata/606150/pfx/drive_c/users/steamuser/AppData/LocalLow/11BitStudios/Moonlighter; do
  [ -d "\$P" ] && echo "SAVEDIR=\$P"
done
for U in "$LINUXFS/root/.local/share/Steam/userdata" "$SDSTEAM/userdata"; do
  for D in "\$U"/*/606150; do [ -d "\$D" ] && echo "SAVEDIR=\$D"; done
done
true
EOF
)
N=0
while IFS= read -r line; do
  D=${line#SAVEDIR=}; [ -n "$D" ] || continue
  N=$((N+1)); F="$BK/save$N.tar"
  adb -s "$SER" exec-out su -c "tar -cf - -C '$D' ." > "$F"
  CNT=$(tar -tf "$F" | wc -l | tr -d ' ')
  [ "$CNT" -gt 1 ] || { echo "[!] backup of $D is empty ($F). Aborting before any write." >&2; exit 6; }
  echo "$D" > "$F.src"
  echo "[+] backed up $D -> $F ($CNT entries, sha256 $(sha_local "$F"))"
done <<<"$SAVES"
[ "$N" -gt 0 ] || echo "[!] WARNING: no save directory found (searched LocalLow/11BitStudios/Moonlighter in the SD and internal prefixes, and Steam userdata/*/606150). Continuing: a fresh install has none. The 2026-10-22 --no-steam run should have left gameslot + options.ini, so if this is the same device, check the prefix path." >&2

# --- restore a save backup instead of deploying a dll (the state being replaced was just backed up above too)
if [ "$MODE" = "--restore-saves" ]; then
  for T in "$RBK"/save*.tar; do
    D=$(cat "$T.src"); [ -n "$D" ] || { echo "[!] $T.src missing" >&2; exit 6; }
    adb -s "$SER" push "$T" /data/local/tmp/ml_save.tar >/dev/null
    rsh <<EOF
set -e
mkdir -p /data/local/tmp/ml-pre-restore-$TS
mv "$D" /data/local/tmp/ml-pre-restore-$TS/$(basename "$T" .tar)
mkdir "$D"
tar -xpf /data/local/tmp/ml_save.tar -C "$D"
chown -R 10114:10114 "$D"
chmod 700 "$D"
rm -f /data/local/tmp/ml_save.tar
EOF
    V="$BK/verify-$(basename "$T")"
    adb -s "$SER" exec-out su -c "tar -cf - -C '$D' ." > "$V"
    A=$(tar -xOf "$T" | shasum -a 256 | cut -d' ' -f1); B=$(tar -xOf "$V" | shasum -a 256 | cut -d' ' -f1)
    LA=$(tar -tvf "$T" | awk '{print $1, $5, $NF}' | sort); LB=$(tar -tvf "$V" | awk '{print $1, $5, $NF}' | sort)
    { [ "$A" = "$B" ] && [ "$LA" = "$LB" ]; } || { echo "[!] restored $D does not match $T (old state kept at /data/local/tmp/ml-pre-restore-$TS)" >&2; exit 6; }
    echo "[+] restored $D from $T (content + listing identical; previous state at /data/local/tmp/ml-pre-restore-$TS)"
  done
  echo "[ok] saves restored from $RBK. Lock released on exit."; exit 0
fi

# --- push: stage, root-copy next to the target, chown/chmod to what was there, swap in, verify
if [ -z "$SRC" ]; then
  ACT="cp \"$DLL.bak\" \"$DLL.new\""      # rollback: .bak is read, never written
  echo "[+] rollback: restore pristine from .bak"
else
  adb -s "$SER" push "$SRC" "$STAGE" >/dev/null
  ACT="cp $STAGE \"$DLL.new\""
  echo "[+] staged $SRC -> $STAGE"
fi
rsh <<EOF
set -e
$ACT
chown $UIDGID "$DLL.new"
chmod $PERM "$DLL.new"
mv -f "$DLL.new" "$DLL"
rm -f $STAGE
EOF
NEW=$(rsh <<EOF
sha256sum "$DLL" | cut -d' ' -f1
EOF
)
BAK2=$(rsh <<EOF
sha256sum "$DLL.bak" | cut -d' ' -f1
EOF
)
META2=$(rsh <<EOF
stat -c '%u:%g %a' "$DLL"
EOF
)
echo "[+] live dll sha256: $NEW"
echo "[+] want:            $WANT"
echo "[+] owner/mode:      $META2 (was $META)"
echo "[+] .bak sha256:     $BAK2"
[ "$NEW" = "$WANT" ] || { echo "[!] HASH MISMATCH after deploy. Run: tools/deploy.sh --rollback" >&2; exit 7; }
[ "$META2" = "$META" ] || { echo "[!] owner/mode changed ($META -> $META2); fix with chown/chmod by hand" >&2; exit 7; }
[ "$BAK2" = "$H_PRISTINE" ] || { echo "[!] .bak is no longer pristine" >&2; exit 7; }
echo "[ok] $MODE deployed and verified on both ends. Saves backed up in $BK. Lock released on exit."
