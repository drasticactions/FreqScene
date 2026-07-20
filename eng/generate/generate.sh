#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

git submodule update --init external/projectm
dotnet tool restore

rm -rf src/ProjectMDotNet/Interop/generated
mkdir -p src/ProjectMDotNet/Interop/generated

dotnet tool run ClangSharpPInvokeGenerator -- "@eng/generate/core.rsp"
dotnet tool run ClangSharpPInvokeGenerator -- "@eng/generate/playlist.rsp"

echo "generated files:"
ls src/ProjectMDotNet/Interop/generated
