"""Relocate bundled native dependencies and sign a self-contained Calculator app."""

import argparse
import plistlib
import re
import shutil
import subprocess
from pathlib import Path


def read_dependencies(binary: Path) -> list[str]:
    result = subprocess.run(
        ["otool", "-L", str(binary)], check=True, capture_output=True, text=True
    )
    first_dependency = 2 if binary.suffix == ".dylib" else 1
    return [
        line.strip().split(" (", 1)[0]
        for line in result.stdout.splitlines()[first_dependency:]
    ]


def relocate_dependencies(binary: Path, native_dir: Path) -> None:
    for dependency in read_dependencies(binary):
        if dependency.startswith(("/System/Library/", "/usr/lib/")):
            continue
        target = native_dir / Path(dependency).name
        if not target.is_file():
            # The SDK bundles ICU under unversioned names while its load commands retain the version.
            target = native_dir / re.sub(
                r"\.\d+(?:\.\d+)*\.dylib$", ".dylib", target.name
            )
        if not target.is_file():
            raise ValueError(
                f"Unbundled native dependency: {binary.name}: {dependency}"
            )
        if target == binary:
            continue
        if dependency == f"@executable_path/{target.name}":
            continue
        subprocess.run(
            [
                "install_name_tool",
                "-change",
                dependency,
                f"@executable_path/{target.name}",
                str(binary),
            ],
            check=True,
        )
    if binary.suffix == ".dylib":
        subprocess.run(
            ["install_name_tool", "-id", f"@rpath/{binary.name}", str(binary)],
            check=True,
        )


def copy_runtime_dependencies(binaries: list[Path], native_dir: Path) -> None:
    for binary in binaries:
        for dependency in read_dependencies(binary):
            if not dependency.startswith("/") or dependency.startswith(
                ("/System/Library/", "/usr/lib/")
            ):
                continue
            source = Path(dependency)
            target = native_dir / source.name
            if target.exists():
                continue
            # Malt's .NET runtime links Brotli dynamically; retain its redistribution notice.
            if not source.name.startswith("libbrotli") or not source.is_file():
                raise ValueError(
                    f"Unbundled native dependency: {binary.name}: {dependency}"
                )
            license_file = source.resolve().parent.parent / "LICENSE"
            if not license_file.is_file():
                raise ValueError(f"Missing runtime dependency license: {license_file}")
            shutil.copyfile(source, target)
            shutil.copyfile(
                license_file, native_dir.parent / "Resources" / "Brotli-LICENSE.txt"
            )
            binaries.append(target)
            common_source = source.parent / "libbrotlicommon.1.dylib"
            common_target = native_dir / common_source.name
            if not common_target.exists():
                if not common_source.is_file():
                    raise ValueError(
                        f"Missing Brotli shared dependency: {common_source}"
                    )
                shutil.copyfile(common_source, common_target)
                binaries.append(common_target)


def sign_bundle(bundle: Path, entitlements: Path) -> None:
    native_dir = bundle / "Contents" / "MacOS"
    with (bundle / "Contents" / "Info.plist").open("rb") as stream:
        executable_name = plistlib.load(stream)["CFBundleExecutable"]
    binaries = [native_dir / executable_name, *sorted(native_dir.glob("*.dylib"))]
    copy_runtime_dependencies(binaries, native_dir)
    for binary in binaries:
        relocate_dependencies(binary, native_dir)
    for binary in binaries[1:]:
        subprocess.run(["codesign", "--force", "--sign", "-", str(binary)], check=True)
    subprocess.run(
        [
            "codesign",
            "--force",
            "--sign",
            "-",
            "--options",
            "runtime",
            "--entitlements",
            str(entitlements),
            str(bundle),
        ],
        check=True,
    )
    subprocess.run(
        ["codesign", "--verify", "--deep", "--strict", str(bundle)], check=True
    )
    for binary in binaries:
        external = [
            dependency
            for dependency in read_dependencies(binary)
            if dependency.startswith("/")
            and not dependency.startswith(("/System/Library/", "/usr/lib/"))
        ]
        if external:
            raise ValueError(
                f"Nonportable native dependencies in {binary.name}: {external}"
            )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--entitlements", required=True, type=Path)
    arguments = parser.parse_args()
    sign_bundle(arguments.bundle.resolve(), arguments.entitlements.resolve())


if __name__ == "__main__":
    main()
