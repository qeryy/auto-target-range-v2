#!/usr/bin/env bash
cd "$(dirname "$0")/.." || exit 1
find src -name '*.cs' | sed 's|/|\\|g' > build/_sources_win.txt
printf 'build\\_version_tmp.cs\n' >> build/_sources_win.txt
wc -l < build/_sources_win.txt
head -2 build/_sources_win.txt