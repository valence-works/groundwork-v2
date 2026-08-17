#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

dotnet tool restore
dotnet restore Groundwork.slnx --nologo
dotnet build Groundwork.slnx --configuration Release --no-restore --nologo

package_root="$(dotnet nuget locals global-packages --list | sed -E 's/^global-packages: //')"
if [[ -z "$package_root" || ! -d "$package_root" ]]; then
  echo "Could not resolve the NuGet global-packages directory." >&2
  exit 1
fi

reference_root="docs/portal/.references"
mkdir -p "$reference_root"
cp "$package_root/microsoft.codeanalysis.common/4.14.0/lib/netstandard2.0/Microsoft.CodeAnalysis.dll" "$reference_root/"
cp "$package_root/microsoft.codeanalysis.csharp/4.14.0/lib/netstandard2.0/Microsoft.CodeAnalysis.CSharp.dll" "$reference_root/"
cp "$package_root/microsoft.codeanalysis.workspaces.common/4.14.0/lib/netstandard2.0/Microsoft.CodeAnalysis.Workspaces.dll" "$reference_root/"
cp "$package_root/microsoft.bcl.asyncinterfaces/9.0.0/lib/netstandard2.0/Microsoft.Bcl.AsyncInterfaces.dll" "$reference_root/"
cp "$package_root/humanizer.core/2.14.1/lib/netstandard2.0/Humanizer.dll" "$reference_root/"
cp "$package_root/sqlitepclraw.core/2.1.12/lib/netstandard2.0/SQLitePCLRaw.core.dll" "$reference_root/"
cp "$package_root/sqlitepclraw.bundle_e_sqlite3/2.1.12/lib/netstandard2.0/SQLitePCLRaw.batteries_v2.dll" "$reference_root/"
cp "$package_root/sqlitepclraw.provider.e_sqlite3/2.1.12/lib/netstandard2.0/SQLitePCLRaw.provider.e_sqlite3.dll" "$reference_root/"
cp "$package_root/system.composition.attributedmodel/9.0.0/lib/netstandard2.0/System.Composition.AttributedModel.dll" "$reference_root/"
cp "$package_root/system.composition.runtime/9.0.0/lib/netstandard2.0/System.Composition.Runtime.dll" "$reference_root/"
cp "$package_root/system.composition.typedparts/9.0.0/lib/netstandard2.0/System.Composition.TypedParts.dll" "$reference_root/"
cp "$package_root/system.composition.hosting/9.0.0/lib/netstandard2.0/System.Composition.Hosting.dll" "$reference_root/"

docfx_log="$(mktemp)"
trap 'rm -f "$docfx_log"' EXIT
dotnet docfx docs/portal/docfx.json --warningsAsErrors 2>&1 | tee "$docfx_log"
if grep -Eq '^[[:space:]]*[1-9][0-9]* warning\(s\)' "$docfx_log"; then
  echo "DocFX completed with warnings; documentation is not release-ready." >&2
  exit 1
fi
