#!/bin/sh
set -eu

test "$#" -eq 1
test "$1" = "/app/collector/RuntimeMetricsLab.Collector.dll"
test "$(readlink /proc/self/ns/mnt)" != "$(readlink /proc/1/ns/mnt)"
awk '$5 == "/sys/kernel/tracing" || $5 == "/sys/kernel/tracing/user_events_data" {
    count++; for (i = 7; i <= NF && $i != "-"; i++) if ($i ~ /^(shared|master):/) exit 1
    if (i > NF) exit 1
} END { if (count != 2) exit 1 }' /proc/self/mountinfo

# Docker 29 archive setup cannot prepare a nested writable bind under a read-only parent.
# Only this private bind mount is remounted; never change the tracefs superblock or host mount.
mount -o remount,bind,ro /sys/kernel/tracing
exec setpriv --no-new-privs --bounding-set=-sys_admin,-setpcap --inh-caps=-all --ambient-caps=-all \
    /usr/share/dotnet/dotnet "$1"
