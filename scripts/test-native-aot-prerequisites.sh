#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
mkdir "$fixture/bin"
export HABY_PROBE_FIXTURE="$fixture"
cat > "$fixture/bin/clang" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
if [[ "$*" == '--version' ]]; then echo 'fixture clang'; exit 0; fi
if [[ "$*" != *'-fuse-ld=bfd'* || "$*" != *'-lz'* ]]; then exit 90; fi
if [[ -e "$HABY_PROBE_FIXTURE/broken" ]]; then exit 91; fi
while [[ "$1" != '-o' ]]; do shift; done
printf '#!/usr/bin/env bash\n[[ ! -e "$HABY_PROBE_FIXTURE/runtime-broken" ]]\n' > "$2"
chmod +x "$2"
SH
cat > "$fixture/bin/sudo" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
echo "$*" >> "$HABY_PROBE_FIXTURE/apt-calls"
if [[ -e "$HABY_PROBE_FIXTURE/apt-fails" ]]; then exit 42; fi
if [[ "$*" == *'install'* && ! -e "$HABY_PROBE_FIXTURE/still-broken" ]]; then
    rm -f "$HABY_PROBE_FIXTURE/broken"
fi
SH
chmod +x "$fixture/bin/clang" "$fixture/bin/sudo"
export PATH="$fixture/bin:$PATH"
export GITHUB_ENV="$fixture/github-env"

bash "$script_dir/ensure-native-aot-prerequisites.sh"
[[ ! -e "$fixture/apt-calls" ]]
grep -Fx "HABY_AOT_C_COMPILER=$fixture/bin/clang" "$GITHUB_ENV"

touch "$fixture/broken"
bash "$script_dir/ensure-native-aot-prerequisites.sh"
[[ $(wc -l < "$fixture/apt-calls") -eq 2 ]]

touch "$fixture/broken" "$fixture/apt-fails"
if bash "$script_dir/ensure-native-aot-prerequisites.sh"; then
    echo 'Failed installation was accepted.' >&2; exit 1
fi
rm "$fixture/apt-fails"
touch "$fixture/still-broken"
if bash "$script_dir/ensure-native-aot-prerequisites.sh"; then
    echo 'Broken toolchain after installation was accepted.' >&2; exit 1
fi
rm "$fixture/broken" "$fixture/still-broken"
touch "$fixture/runtime-broken"
if bash "$script_dir/ensure-native-aot-prerequisites.sh"; then
    echo 'Unusable linked executable was accepted.' >&2; exit 1
fi
echo 'Native AOT prerequisite orchestration checks passed.'
