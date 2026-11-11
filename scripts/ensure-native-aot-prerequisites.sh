#!/usr/bin/env bash
set -euo pipefail

# Prove the installed toolchain works before depending on Ubuntu mirror access.
# Publish must use this exact compiler and the same bfd linker selection.
probe_dir=$(mktemp -d)
trap 'rm -rf -- "$probe_dir"' EXIT
cat > "$probe_dir/probe.c" <<'C'
#include <stdio.h>
#include <zlib.h>
int main(void)
{
    const char *version = zlibVersion();
    if (version == NULL || version[0] == '\0') return 1;
    puts(version);
    return 0;
}
C

probe() {
    compiler=$(command -v clang) || return 1
    compiler=$(readlink -f -- "$compiler") || return 1
    timeout 30s "$compiler" --version || return 1
    timeout 30s "$compiler" -v -fuse-ld=bfd -Wall -Wextra -Werror \
        "$probe_dir/probe.c" -lz -o "$probe_dir/probe" || return 1
    timeout 10s "$probe_dir/probe" || return 1
}

if ! probe; then
    echo 'Installed Native AOT prerequisites failed the probe; installing prerequisites.'
    timeout 180s sudo -n apt-get -o Acquire::Retries=1 \
        -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 update
    timeout 180s sudo -n apt-get -o Acquire::Retries=1 \
        -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 install -y clang zlib1g-dev
    if ! probe; then
        echo 'Native AOT prerequisite probe failed after installation.' >&2
        exit 1
    fi
fi

echo "Validated Native AOT compiler: $compiler; linker selection: bfd"
if [[ -n "${GITHUB_ENV:-}" ]]; then
    printf 'HABY_AOT_C_COMPILER=%s\n' "$compiler" >> "$GITHUB_ENV"
fi
