#!/bin/sh
# Builds the library Meitou loads into lib/<rid>/ (needs Rust; Windows from Linux also the x86_64-pc-windows-gnu target and mingw-w64).
set -e
cd "$(dirname "$0")"
cargo build --release -p delirium-sdk
cargo build --release -p delirium-sdk --target x86_64-pc-windows-gnu
mkdir -p lib/linux-x64 lib/win-x64
cp target/release/libdelirium_physics.so lib/linux-x64/
cp target/x86_64-pc-windows-gnu/release/delirium_physics.dll lib/win-x64/
